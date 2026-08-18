import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
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
}): Pick<WorkerApi, "getRepo"> {
  return {
    async getRepo() {
      return {
        id: repo.id,
        project_id: "proj-1",
        provider: repo.provider,
        local_root_hint: repo.local_root_hint,
        index_mode: repo.index_mode,
      };
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
});
