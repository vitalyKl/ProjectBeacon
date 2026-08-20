import { describe, expect, it } from "vitest";

import type { WorkerApi } from "./client.js";
import { WorkerIndexRegistry } from "./index-server.js";
import { purgeDeletedProjectClones } from "./purge.js";

function repoView(id: string) {
  return {
    id,
    project_id: "proj-1",
    provider: "local",
    remote_url: null,
    default_branch: "main",
    installation_id: null,
    local_root_hint: "demo",
    index_mode: "bind_mount",
  };
}

describe("purgeDeletedProjectClones", () => {
  it("drops index files for repos listed on deleted projects", async () => {
    const dropped: string[] = [];
    const api = {
      async listDeletedProjects() {
        return [{ id: "proj-1", deleted_at: "2026-01-01T00:00:00.000Z" }];
      },
      async listProjectRepos() {
        return [repoView("repo-1"), repoView("repo-2")];
      },
    } as Pick<WorkerApi, "listDeletedProjects" | "listProjectRepos">;
    const registry = {
      async dropRepo(repoId: string) {
        dropped.push(repoId);
      },
    } as Pick<WorkerIndexRegistry, "dropRepo">;

    await expect(
      purgeDeletedProjectClones(api as WorkerApi, registry as WorkerIndexRegistry),
    ).resolves.toEqual(["repo-1", "repo-2"]);
    expect(dropped).toEqual(["repo-1", "repo-2"]);
  });
});
