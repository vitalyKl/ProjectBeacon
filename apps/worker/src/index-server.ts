import path from "node:path";

import { IndexCore, listenIndexHttp } from "@beacon/index-core";

import { parseLocalRootHint } from "./local-root.js";
import type { WorkerApi } from "./client.js";
import type { WorkerConfig } from "./config.js";

export type IndexedRepo = {
  core: IndexCore;
  lastIndexedAt: Date | null;
};

export class WorkerIndexRegistry {
  private readonly cores = new Map<string, IndexedRepo>();

  constructor(private readonly options: { workspace?: string; indexDir: string }) {}

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

  async ensureIndexed(repo: {
    id: string;
    provider: string;
    index_mode: string;
    local_root_hint: string | null;
  }): Promise<IndexCore | undefined> {
    if (repo.provider !== "local") {
      return this.get(repo.id);
    }
    if (repo.index_mode !== "bind_mount" && repo.index_mode !== "both") {
      return this.get(repo.id);
    }
    if (!this.options.workspace) {
      return this.get(repo.id);
    }
    const hint = parseLocalRootHint(repo.local_root_hint ?? "");
    if (!hint) {
      return this.get(repo.id);
    }
    const existing = this.cores.get(repo.id);
    if (existing) {
      existing.core.index();
      existing.lastIndexedAt = existing.core.lastIndexedAt();
      return existing.core;
    }
    const repoRoot =
      hint === "."
        ? path.resolve(this.options.workspace)
        : path.resolve(this.options.workspace, ...hint.split("/"));
    const workspaceRoot = path.resolve(this.options.workspace);
    if (repoRoot !== workspaceRoot && !repoRoot.startsWith(workspaceRoot + path.sep)) {
      return undefined;
    }
    const core = new IndexCore({
      repoRoot,
      dbPath: path.join(this.options.indexDir, `${repo.id}.sqlite`),
    });
    core.index();
    this.cores.set(repo.id, { core, lastIndexedAt: core.lastIndexedAt() });
    return core;
  }
}

export async function startWorkerIndexHttp(
  config: WorkerConfig,
  registry: WorkerIndexRegistry,
): Promise<{ close: () => Promise<void> }> {
  const { server } = await listenIndexHttp({
    host: config.indexRpcHost,
    port: config.indexRpcPort,
    auth: config.indexRpcToken ? { token: config.indexRpcToken } : undefined,
    resolve: (repoId) => registry.get(repoId),
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
  await registry.ensureIndexed(repo);
}
