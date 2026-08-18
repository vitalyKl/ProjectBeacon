import { mkdtemp, mkdir, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import { describe, expect, it } from "vitest";

import type { WorkerApi } from "./client.js";
import { refreshBindMountIndex, WorkerIndexRegistry } from "./index-server.js";

function repoApi(repo: {
  id: string;
  provider: string;
  index_mode: string;
  local_root_hint: string | null;
}): Pick<WorkerApi, "getRepo" | "consumeCloneInvalidation"> {
  return {
    async getRepo() {
      return {
        id: repo.id,
        project_id: "proj-1",
        provider: repo.provider,
        remote_url: null,
        default_branch: "main",
        installation_id: null,
        local_root_hint: repo.local_root_hint,
        index_mode: repo.index_mode,
      };
    },
    async consumeCloneInvalidation() {
      return { consumed: null };
    },
  };
}

describe("WorkerIndexRegistry", () => {
  it("reopens a persisted bind_mount sqlite without a detect job", async () => {
    const root = await mkdtemp(path.join(tmpdir(), "beacon-index-rpc-"));
    const workspace = path.join(root, "workspace");
    const indexDir = path.join(root, "index");
    await mkdir(path.join(workspace, "demo"), { recursive: true });
    await mkdir(indexDir, { recursive: true });
    await writeFile(path.join(workspace, "demo", "hello.ts"), "export const hello = 1;\n");

    const repo = {
      id: "01934567-89ab-7cde-89ab-0123456789aa",
      provider: "local",
      index_mode: "bind_mount",
      local_root_hint: "demo",
    };
    const first = new WorkerIndexRegistry({ workspace, indexDir });
    const written = await first.ensureIndexed(repo);
    expect(written).toBeDefined();
    expect(written?.getTree({ root: ".", depth: 1 }).length).toBeGreaterThan(0);
    first.close();

    const second = new WorkerIndexRegistry({ workspace, indexDir });
    expect(second.get(repo.id)).toBeUndefined();
    await refreshBindMountIndex(repoApi(repo) as WorkerApi, second, repo.id);
    const reopened = second.get(repo.id);
    expect(reopened).toBeDefined();
    expect(reopened?.getTree({ root: ".", depth: 1 }).some((dir) => dir.path === ".")).toBe(true);
    second.close();
  });

  it("drops a hosted clone tree and sqlite for a repo id", async () => {
    const root = await mkdtemp(path.join(tmpdir(), "beacon-hosted-drop-"));
    const indexDir = path.join(root, "index");
    const cloneDir = path.join(root, "clones");
    const repoId = "01934567-89ab-7cde-89ab-0123456789bb";
    await mkdir(path.join(cloneDir, repoId), { recursive: true });
    await mkdir(indexDir, { recursive: true });
    await writeFile(path.join(cloneDir, repoId, "hello.ts"), "export const hello = 1;\n");
    await writeFile(path.join(indexDir, `${repoId}.sqlite`), "sqlite");
    const registry = new WorkerIndexRegistry({ indexDir, cloneDir });
    await registry.dropRepo(repoId);
    await expect(stat(path.join(cloneDir, repoId))).rejects.toMatchObject({ code: "ENOENT" });
    await expect(stat(path.join(indexDir, `${repoId}.sqlite`))).rejects.toMatchObject({
      code: "ENOENT",
    });
    registry.close();
  });
});
