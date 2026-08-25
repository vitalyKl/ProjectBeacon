import { randomBytes } from "node:crypto";
import { lstatSync, readdirSync, statSync } from "node:fs";
import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";

import {
  IndexCore,
  handleIndexRequest,
  type IndexCoreWithFreshness,
  type MtimeState,
} from "@beacon/index-core";

import { apiGet } from "./api.js";
import { resolveBeaconHome } from "./home.js";

export const SIDECAR_FILE_NAME = "sidecar.json";
export const HEARTBEAT_MS = 20_000;

export type SidecarFile = {
  host: string;
  port: number;
  token: string;
  pid: number;
};

export type SidecarOptions = {
  home: string;
  cwd: string;
  url: string;
  token: string;
  projectId?: string;
  host?: string;
  port?: number;
  fetchImpl?: typeof fetch;
  now?: () => Date;
  heartbeatMs?: number;
};

export function sidecarPath(home: string): string {
  return path.join(home, SIDECAR_FILE_NAME);
}

export function parseSidecarFile(source: string): SidecarFile | undefined {
  try {
    const parsed = JSON.parse(source) as Partial<SidecarFile>;
    if (
      typeof parsed.host !== "string" ||
      typeof parsed.port !== "number" ||
      typeof parsed.token !== "string" ||
      typeof parsed.pid !== "number"
    ) {
      return undefined;
    }
    return { host: parsed.host, port: parsed.port, token: parsed.token, pid: parsed.pid };
  } catch {
    return undefined;
  }
}

export async function readSidecarFile(home: string): Promise<SidecarFile | undefined> {
  try {
    return parseSidecarFile(await readFile(sidecarPath(home), "utf8"));
  } catch {
    return undefined;
  }
}

function buildMtimeState(repoRoot: string): MtimeState {
  const state: MtimeState = { rootMtime: 0, dirMtimes: new Map() };
  try {
    const rootStat = statSync(repoRoot);
    state.rootMtime = rootStat.mtimeMs;
    const entries = readdirSync(repoRoot);
    for (const entry of entries) {
      try {
        const entryPath = path.join(repoRoot, entry);
        const st = lstatSync(entryPath);
        if (st.isDirectory()) {
          state.dirMtimes.set(entryPath, st.mtimeMs);
        }
      } catch {
        // ignore unreadable entries
      }
    }
  } catch {
    // ignore errors during mtime scan
  }
  return state;
}

function hasMtimeChanged(repoRoot: string, state: MtimeState): boolean {
  try {
    const rootStat = statSync(repoRoot);
    if (rootStat.mtimeMs !== state.rootMtime) {
      return true;
    }
    for (const [dirPath, prevMtime] of state.dirMtimes) {
      try {
        const st = lstatSync(dirPath);
        if (st.mtimeMs !== prevMtime) {
          return true;
        }
      } catch {
        // directory may have been deleted, reindex
        return true;
      }
    }
  } catch {
    // root may have been moved/deleted, reindex
  }
  return false;
}

export async function writeSidecarFile(home: string, file: SidecarFile): Promise<void> {
  await mkdir(home, { recursive: true });
  const dest = sidecarPath(home);
  await writeFile(dest, `${JSON.stringify(file, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
  if (process.platform !== "win32") {
    await chmod(dest, 0o600);
  }
}

async function listProjectRepos(
  url: string,
  token: string,
  projectId: string,
  fetchImpl: typeof fetch,
): Promise<{ id: string }[]> {
  const listed = await apiGet(url, token, `/v1/projects/${projectId}/repos`, fetchImpl);
  if (listed.status < 200 || listed.status >= 300) {
    return [];
  }
  const body = listed.body;
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return [];
  }
  const items = (body as { items?: unknown }).items;
  if (!Array.isArray(items)) {
    return [];
  }
  return items.flatMap((item) => {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return [];
    }
    const id = (item as { id?: unknown }).id;
    return typeof id === "string" ? [{ id }] : [];
  });
}

async function registerHeartbeats(options: SidecarOptions, repoIds: string[]): Promise<void> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const base = options.url.replace(/\/+$/, "");
  await Promise.all(
    repoIds.map(async (repoId) => {
      try {
        await fetchImpl(`${base}/v1/repos/${repoId}/sidecar/register`, {
          method: "POST",
          headers: {
            authorization: `Bearer ${options.token}`,
            accept: "application/json",
          },
        });
      } catch {
        // heartbeat is best-effort
      }
    }),
  );
}

export async function startSidecar(options: SidecarOptions): Promise<{
  host: string;
  port: number;
  token: string;
  close: () => Promise<void>;
}> {
  const host = options.host ?? "127.0.0.1";
  if (host === "0.0.0.0" || host === "::" || host === "[::]") {
    throw new Error("sidecar must bind 127.0.0.1");
  }
  const token = randomBytes(24).toString("base64url");
  const cores = new Map<string, IndexCoreWithFreshness>();
  const home = options.home || resolveBeaconHome();
  const defaultRepoId = options.projectId ? `local:${options.projectId}` : "local";

  const createCore = (repoId: string): IndexCoreWithFreshness => {
    const core = new IndexCore({
      repoRoot: options.cwd,
      dbPath: path.join(home, "index", `${repoId}.sqlite`),
    });
    core.index();
    return { core, lastIndexedAt: core.lastIndexedAt(), mtime: buildMtimeState(options.cwd) };
  };

  const server = createServer((req, res) => {
    handleIndexRequest(req, res, {
      auth: { token },
      resolve: (repoId) => {
        const existing = cores.get(repoId);
        if (existing) {
          return existing;
        }
        const core = createCore(repoId);
        cores.set(repoId, core);
        return core;
      },
      onQuery: (coreState) => {
        if (hasMtimeChanged(options.cwd, coreState.mtime)) {
          coreState.core.index();
          coreState.lastIndexedAt = coreState.core.lastIndexedAt();
          const fresh = buildMtimeState(options.cwd);
          coreState.mtime.rootMtime = fresh.rootMtime;
          coreState.mtime.dirMtimes = fresh.dirMtimes;
        }
      },
    });
  });

  const bound = await new Promise<{ port: number }>((resolve, reject) => {
    server.once("error", reject);
    server.listen(options.port ?? 0, host, () => {
      server.off("error", reject);
      const address = server.address();
      resolve({
        port: address && typeof address === "object" ? address.port : (options.port ?? 0),
      });
    });
  });

  await writeSidecarFile(home, {
    host,
    port: bound.port,
    token,
    pid: process.pid,
  });

  let timer: NodeJS.Timeout | undefined;
  if (options.projectId) {
    const fetchImpl = options.fetchImpl ?? fetch;
    const tick = async () => {
      const repos = await listProjectRepos(
        options.url,
        options.token,
        options.projectId!,
        fetchImpl,
      );
      const ids = repos.length > 0 ? repos.map((repo) => repo.id) : [defaultRepoId];
      await registerHeartbeats(options, ids);
    };
    await tick();
    timer = setInterval(() => {
      void tick();
    }, options.heartbeatMs ?? HEARTBEAT_MS);
    timer.unref?.();
  }

  return {
    host,
    port: bound.port,
    token,
    close: async () => {
      if (timer) {
        clearInterval(timer);
      }
      for (const entry of cores.values()) {
        entry.core.close();
      }
      await new Promise<void>((resolve, reject) => {
        server.close((error) => {
          if (error) {
            reject(error);
            return;
          }
          resolve();
        });
      });
    },
  };
}
