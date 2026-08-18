import { randomBytes } from "node:crypto";
import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";

import { IndexCore, handleIndexRequest } from "@beacon/index-core";

import { apiGet } from "./api.js";
import { resolveBeaconHome } from "./home.js";
import {
  applyQuery,
  defaultSidecarTunnelDialer,
  sidecarTunnelUrl,
  type SidecarTunnelDialer,
  type TunnelRpcQuery,
} from "./tunnel.js";

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
  beaconHost?: string;
  tunnelEnabled?: boolean;
  tunnelDialer?: SidecarTunnelDialer;
};

export function sidecarTunnelOptedIn(options: {
  beaconHost?: string;
  tunnelEnabled?: boolean;
}): boolean {
  if (options.tunnelEnabled === true) {
    return true;
  }
  return Boolean(options.beaconHost?.trim());
}

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

type TunnelRequest = {
  type: "req";
  id: string;
  repo_id: string;
  path: string;
  query?: TunnelRpcQuery;
};

function asTunnelRequest(value: unknown): TunnelRequest | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const record = value as {
    type?: unknown;
    id?: unknown;
    repo_id?: unknown;
    path?: unknown;
    query?: unknown;
  };
  if (
    record.type !== "req" ||
    typeof record.id !== "string" ||
    typeof record.repo_id !== "string" ||
    typeof record.path !== "string"
  ) {
    return undefined;
  }
  return {
    type: "req",
    id: record.id,
    repo_id: record.repo_id,
    path: record.path,
    query:
      record.query !== null && typeof record.query === "object" && !Array.isArray(record.query)
        ? (record.query as TunnelRpcQuery)
        : undefined,
  };
}

async function proxyLocalIndex(options: {
  host: string;
  port: number;
  token: string;
  repoId: string;
  path: string;
  query?: TunnelRpcQuery;
  fetchImpl: typeof fetch;
}): Promise<{ status: number; body: unknown }> {
  const url = new URL(
    `http://${options.host}:${options.port}/repos/${options.repoId}${options.path}`,
  );
  applyQuery(url, options.query);
  try {
    const response = await options.fetchImpl(url, {
      method: "GET",
      headers: {
        authorization: `Bearer ${options.token}`,
        accept: "application/json",
      },
    });
    const text = await response.text();
    let parsed: unknown;
    if (text.length > 0) {
      try {
        parsed = JSON.parse(text) as unknown;
      } catch {
        parsed = undefined;
      }
    }
    return { status: response.status, body: parsed };
  } catch {
    return {
      status: 503,
      body: { error: { code: "code_index_unavailable", message: "code index unavailable" } },
    };
  }
}

function startOutboundTunnel(options: {
  controlUrl: string;
  token: string;
  beaconHost?: string;
  repoIds: () => string[];
  local: { host: string; port: number; token: string };
  fetchImpl: typeof fetch;
  dialer: SidecarTunnelDialer;
}): { close: () => void } {
  const url = sidecarTunnelUrl(options.controlUrl, options.beaconHost);
  const socket = options.dialer(url, { authorization: `Bearer ${options.token}` });

  socket.on("open", () => {
    socket.send(
      JSON.stringify({ type: "hello", token: options.token, repo_ids: options.repoIds() }),
    );
  });
  socket.on("message", (raw) => {
    const text =
      typeof raw === "string"
        ? raw
        : raw instanceof ArrayBuffer
          ? Buffer.from(raw).toString("utf8")
          : Buffer.isBuffer(raw)
            ? raw.toString("utf8")
            : undefined;
    if (!text) {
      return;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(text) as unknown;
    } catch {
      return;
    }
    const request = asTunnelRequest(parsed);
    if (!request) {
      return;
    }
    void proxyLocalIndex({
      host: options.local.host,
      port: options.local.port,
      token: options.local.token,
      repoId: request.repo_id,
      path: request.path,
      query: request.query,
      fetchImpl: options.fetchImpl,
    }).then((result) => {
      socket.send(
        JSON.stringify({
          type: "res",
          id: request.id,
          status: result.status,
          body: result.body,
        }),
      );
    });
  });
  socket.on("error", () => {
    // outbound tunnel is best-effort next to local HTTP
  });

  return {
    close() {
      socket.close();
    },
  };
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
  const cores = new Map<string, IndexCore>();
  const home = options.home || resolveBeaconHome();
  const defaultRepoId = options.projectId ? `local:${options.projectId}` : "local";

  const server = createServer((req, res) => {
    handleIndexRequest(req, res, {
      auth: { token },
      resolve: (repoId) => {
        const existing = cores.get(repoId);
        if (existing) {
          return existing;
        }
        const core = new IndexCore({
          repoRoot: options.cwd,
          dbPath: path.join(home, "index", `${repoId}.sqlite`),
        });
        core.index();
        cores.set(repoId, core);
        return core;
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
  let tunnel: { close: () => void } | undefined;
  let liveRepoIds = options.projectId ? [defaultRepoId] : [];
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
      liveRepoIds = ids;
      await registerHeartbeats(options, ids);
    };
    await tick();
    timer = setInterval(() => {
      void tick();
    }, options.heartbeatMs ?? HEARTBEAT_MS);
    timer.unref?.();
    if (sidecarTunnelOptedIn(options)) {
      tunnel = startOutboundTunnel({
        controlUrl: options.url,
        token: options.token,
        beaconHost: options.beaconHost,
        repoIds: () => liveRepoIds,
        local: { host, port: bound.port, token },
        fetchImpl,
        dialer: options.tunnelDialer ?? defaultSidecarTunnelDialer,
      });
    }
  }

  return {
    host,
    port: bound.port,
    token,
    close: async () => {
      if (timer) {
        clearInterval(timer);
      }
      tunnel?.close();
      for (const core of cores.values()) {
        core.close();
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
