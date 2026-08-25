import { rm } from "node:fs/promises";
import path from "node:path";

import { IndexCore, listenIndexHttp } from "@beacon/index-core";

import { ensureHostedClone, removeHostedClone } from "./clone.js";
import type { WorkerApi } from "./client.js";
import type { WorkerConfig } from "./config.js";
import type { GithubAppConfig } from "./github-app.js";
import { parseLocalRootHint } from "./local-root.js";

export type IndexedRepo = {
  core: IndexCore;
  lastIndexedAt: Date | null;
};

export type IndexableRepo = {
  id: string;
  project_id?: string;
  provider: string;
  index_mode: string;
  local_root_hint: string | null;
  remote_url?: string | null;
  default_branch?: string;
  installation_id?: string | null;
};

export class WorkerIndexRegistry {
  private readonly cores = new Map<string, IndexedRepo>();

  constructor(
    private readonly options: {
      workspace?: string;
      indexDir: string;
      cloneDir?: string;
      githubApp?: GithubAppConfig;
      fetchImpl?: typeof fetch;
    },
  ) {}

  get(repoId: string): IndexCore | undefined {
    return this.cores.get(repoId)?.core;
  }

  lastIndexedAt(repoId: string): Date | null {
    return this.cores.get(repoId)?.lastIndexedAt ?? null;
  }

  close(): void {
    for (const entry of this.cores.values()) {
      entry.core.close();
    }
    this.cores.clear();
  }

  async dropRepo(repoId: string): Promise<void> {
    const existing = this.cores.get(repoId);
    if (existing) {
      existing.core.close();
      this.cores.delete(repoId);
    }
    await rm(path.join(this.options.indexDir, `${repoId}.sqlite`), { force: true });
    await rm(path.join(this.options.indexDir, `${repoId}.sqlite-wal`), { force: true });
    await rm(path.join(this.options.indexDir, `${repoId}.sqlite-shm`), { force: true });
    if (this.options.cloneDir) {
      await removeHostedClone(this.options.cloneDir, repoId);
    }
  }

  private usesHostedClone(repo: IndexableRepo): boolean {
    return (
      repo.provider === "github" &&
      (repo.index_mode === "hosted_clone" || repo.index_mode === "both")
    );
  }

  private usesBindMount(repo: IndexableRepo): boolean {
    return (
      repo.provider === "local" && (repo.index_mode === "bind_mount" || repo.index_mode === "both")
    );
  }

  private openCore(repoId: string, repoRoot: string): IndexCore {
    const existing = this.cores.get(repoId);
    if (existing) {
      existing.core.index();
      existing.lastIndexedAt = existing.core.lastIndexedAt();
      return existing.core;
    }
    const core = new IndexCore({
      repoRoot,
      dbPath: path.join(this.options.indexDir, `${repoId}.sqlite`),
    });
    core.index();
    this.cores.set(repoId, { core, lastIndexedAt: core.lastIndexedAt() });
    return core;
  }

  async ensureIndexed(
    repo: IndexableRepo,
    options: { forceClone?: boolean } = {},
  ): Promise<IndexCore | undefined> {
    if (this.usesBindMount(repo)) {
      if (!this.options.workspace) {
        return this.get(repo.id);
      }
      const hint = parseLocalRootHint(repo.local_root_hint ?? "");
      if (!hint) {
        return this.get(repo.id);
      }
      const repoRoot =
        hint === "."
          ? path.resolve(this.options.workspace)
          : path.resolve(this.options.workspace, ...hint.split("/"));
      const workspaceRoot = path.resolve(this.options.workspace);
      if (repoRoot !== workspaceRoot && !repoRoot.startsWith(workspaceRoot + path.sep)) {
        return undefined;
      }
      return this.openCore(repo.id, repoRoot);
    }

    if (!this.usesHostedClone(repo)) {
      return this.get(repo.id);
    }
    if (!this.options.cloneDir || !this.options.githubApp) {
      return this.get(repo.id);
    }
    if (options.forceClone) {
      const existing = this.cores.get(repo.id);
      if (existing) {
        existing.core.close();
        this.cores.delete(repo.id);
      }
    }
    const repoRoot = await ensureHostedClone(
      {
        id: repo.id,
        provider: repo.provider,
        remote_url: repo.remote_url ?? null,
        default_branch: repo.default_branch ?? "main",
        installation_id: repo.installation_id ?? null,
      },
      {
        cloneDir: this.options.cloneDir,
        githubApp: this.options.githubApp,
        force: options.forceClone,
        fetchImpl: this.options.fetchImpl,
      },
    );
    return this.openCore(repo.id, repoRoot);
  }
}

export async function startWorkerIndexHttp(
  config: WorkerConfig,
  registry: WorkerIndexRegistry,
  api: WorkerApi,
): Promise<{ close: () => Promise<void> }> {
  const { server } = await listenIndexHttp({
    host: config.indexRpcHost,
    port: config.indexRpcPort,
    auth: config.indexRpcToken ? { token: config.indexRpcToken } : undefined,
    resolve: async (repoId) => {
      const existing = registry.get(repoId);
      if (existing) {
        return {
          core: existing,
          lastIndexedAt: registry.lastIndexedAt(repoId),
          mtime: { rootMtime: 0, dirMtimes: new Map() },
        };
      }
      try {
        await refreshBindMountIndex(api, registry, repoId);
      } catch {
        return undefined;
      }
      const refreshed = registry.get(repoId);
      if (!refreshed) {
        return undefined;
      }
      return {
        core: refreshed,
        lastIndexedAt: registry.lastIndexedAt(repoId),
        mtime: { rootMtime: 0, dirMtimes: new Map() },
      };
    },
  });
  return {
    close: () =>
      new Promise((resolve, reject) => {
        server.close((error) => {
          if (error) {
            reject(error);
            return;
          }
          resolve();
        });
      }),
  };
}

export async function refreshBindMountIndex(
  api: WorkerApi,
  registry: WorkerIndexRegistry,
  repoId: string,
): Promise<void> {
  const repo = await api.getRepo(repoId);
  let forceClone = false;
  if (repo.index_mode === "hosted_clone" || repo.index_mode === "both") {
    const pending = await api.consumeCloneInvalidation(repo.id);
    forceClone = Boolean(pending.consumed);
  }
  await registry.ensureIndexed(repo, { forceClone });
}
