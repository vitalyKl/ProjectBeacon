import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import { describe, expect, it } from "vitest";

import type { WorkerApi } from "./client.js";
import { runDetectJob } from "./detect.js";

function mockApi(overrides: Partial<WorkerApi> = {}): WorkerApi & {
  imports: { projectId: string; repoId: string; files: { path: string; content: string }[] }[];
  milestones: string[];
  tasks: string[];
} {
  const imports: {
    projectId: string;
    repoId: string;
    files: { path: string; content: string }[];
  }[] = [];
  const milestones: string[] = [];
  const tasks: string[] = [];
  return {
    imports,
    milestones,
    tasks,
    async getRepo() {
      return {
        id: "repo-1",
        project_id: "proj-1",
        provider: "local",
        local_root_hint: "demo",
        index_mode: "bind_mount",
      };
    },
    async importContext(projectId, repoId, files) {
      imports.push({ projectId, repoId, files });
      return {};
    },
    async createMilestone(_projectId, body) {
      milestones.push(body.title);
      return { id: "ms-1" };
    },
    async createTask(_projectId, body) {
      tasks.push(body.title);
      return { id: `task-${tasks.length}` };
    },
    ...overrides,
  };
}

describe("runDetectJob", () => {
  it("imports scanned files and posts a milestone plus detector tasks", async () => {
    const workspace = await mkdtemp(path.join(tmpdir(), "beacon-detect-"));
    await mkdir(path.join(workspace, "demo"));
    await writeFile(path.join(workspace, "demo", "AGENTS.md"), "## Goals\nShip it.\n");
    await writeFile(
      path.join(workspace, "demo", "package.json"),
      JSON.stringify({ name: "demo", dependencies: { typescript: "5.0.0" } }),
    );

    const api = mockApi();
    const result = await runDetectJob(
      { repo_id: "repo-1", project_id: "proj-1" },
      { api, workspace },
    );

    expect(result.imported).toBe(2);
    expect(api.imports[0]?.files.map((file) => file.path).sort()).toEqual([
      "AGENTS.md",
      "package.json",
    ]);
    expect(api.milestones).toEqual(["Detector skeleton"]);
    expect(api.tasks).toEqual(
      expect.arrayContaining([
        "Review imported project context",
        "Confirm Node workspace layout",
        "Merge imported agent rules",
      ]),
    );
  });

  it("still posts a skeleton when the workspace is missing", async () => {
    const api = mockApi();
    const result = await runDetectJob({ repo_id: "repo-1", project_id: "proj-1" }, { api });
    expect(result.imported).toBe(0);
    expect(api.imports).toHaveLength(0);
    expect(api.milestones).toHaveLength(1);
    expect(api.tasks.length).toBeGreaterThan(0);
  });
});
