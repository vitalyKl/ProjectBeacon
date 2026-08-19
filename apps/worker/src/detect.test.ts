import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import { describe, expect, it } from "vitest";

import type { LabelView, WorkerApi } from "./client.js";
import { runDetectJob } from "./detect.js";

function mockApi(overrides: Partial<WorkerApi> = {}): WorkerApi & {
  imports: { projectId: string; repoId: string; files: { path: string; content: string }[] }[];
  milestones: string[];
  tasks: string[];
  created: Array<{ title: string; label_ids?: string[] }>;
  labels: LabelView[];
  patches: { id: string; paths: { repo_id: string; path: string }[] }[];
} {
  const imports: {
    projectId: string;
    repoId: string;
    files: { path: string; content: string }[];
  }[] = [];
  const milestones: { id: string; title: string }[] = [];
  const tasks: { id: string; title: string; milestone_id: string | null }[] = [];
  const created: Array<{ title: string; label_ids?: string[] }> = [];
  const labels: LabelView[] = [];
  const patches: { id: string; paths: { repo_id: string; path: string }[] }[] = [];
  return {
    imports,
    get created() {
      return created;
    },
    get labels() {
      return labels;
    },
    get patches() {
      return patches;
    },
    get milestones() {
      return milestones.map((row) => row.title);
    },
    get tasks() {
      return tasks.map((row) => row.title);
    },
    async getRepo() {
      return {
        id: "repo-1",
        project_id: "proj-1",
        provider: "local",
        remote_url: null,
        default_branch: "main",
        installation_id: null,
        local_root_hint: "demo",
        index_mode: "bind_mount",
      };
    },
    async reportIndex() {
      return {};
    },
    async consumeCloneInvalidation() {
      return { consumed: null };
    },
    async listDeletedProjects() {
      return [];
    },
    async projectClonePurge() {
      return { project_id: "proj-1", deleted: false, repo_ids: [] };
    },
    async importContext(projectId, repoId, files) {
      imports.push({ projectId, repoId, files });
      return {};
    },
    async listMilestones() {
      return milestones.map((row) => ({ ...row }));
    },
    async createMilestone(_projectId, body) {
      const created = { id: `ms-${milestones.length + 1}`, title: body.title };
      milestones.push(created);
      return created;
    },
    async listTasks() {
      return tasks.map((row) => ({ ...row }));
    },
    async createTask(_projectId, body) {
      const next = {
        id: `task-${tasks.length + 1}`,
        title: body.title,
        milestone_id: body.milestone_id ?? null,
      };
      tasks.push(next);
      created.push({ title: body.title, label_ids: body.label_ids });
      return next;
    },
    async listLabels() {
      return labels.map((row) => ({ ...row, paths: row.paths.map((path) => ({ ...path })) }));
    },
    async patchLabel(labelId, body) {
      const current = labels.find((row) => row.id === labelId);
      if (!current) {
        throw new Error(`missing label ${labelId}`);
      }
      current.paths = (body.paths ?? []).map((path) => ({ ...path }));
      patches.push({ id: labelId, paths: current.paths.map((path) => ({ ...path })) });
      return { ...current, paths: current.paths.map((path) => ({ ...path })) };
    },
    async listGithubIssues() {
      return [];
    },
    async getGithubIssue() {
      return undefined;
    },
    async upsertImportedIssues() {
      return { count: 0 };
    },
    async recordGithubInvalidation() {
      return { recorded: true };
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

  it("reuses the detector milestone and does not duplicate tasks on retry", async () => {
    const api = mockApi();
    const first = await runDetectJob({ repo_id: "repo-1", project_id: "proj-1" }, { api });
    const second = await runDetectJob({ repo_id: "repo-1", project_id: "proj-1" }, { api });
    expect(first.milestone_id).toBe(second.milestone_id);
    expect(api.milestones).toEqual(["Detector skeleton"]);
    expect(second.tasks).toBe(0);
    expect(api.tasks).toEqual(["Review imported project context"]);
  });

  it("binds suggested prefixes onto seeded areas that have none for the repo", async () => {
    const workspace = await mkdtemp(path.join(tmpdir(), "beacon-detect-areas-"));
    await mkdir(path.join(workspace, "demo", "apps", "api"), { recursive: true });
    await mkdir(path.join(workspace, "demo", "apps", "web"), { recursive: true });
    await writeFile(path.join(workspace, "demo", "apps", "api", "package.json"), "{}");
    await writeFile(path.join(workspace, "demo", "apps", "web", "package.json"), "{}");

    const api = mockApi();
    api.labels.push(
      {
        id: "lab-api",
        slug: "api",
        name: "API",
        status: "active",
        paths: [],
      },
      {
        id: "lab-web",
        slug: "web",
        name: "Web",
        status: "active",
        paths: [{ repo_id: "repo-1", path: "frontend" }],
      },
      {
        id: "lab-cli",
        slug: "cli",
        name: "CLI",
        status: "active",
        paths: [],
      },
    );

    await runDetectJob({ repo_id: "repo-1", project_id: "proj-1" }, { api, workspace });

    expect(api.patches).toEqual([
      { id: "lab-api", paths: [{ repo_id: "repo-1", path: "apps/api" }] },
    ]);
    expect(api.labels.find((label) => label.id === "lab-web")?.paths).toEqual([
      { repo_id: "repo-1", path: "frontend" },
    ]);
    expect(api.created.find((task) => task.title === "Confirm Node workspace layout")).toEqual({
      title: "Confirm Node workspace layout",
      label_ids: ["lab-api"],
    });
    expect(api.created.find((task) => task.title === "Review imported project context")).toEqual({
      title: "Review imported project context",
      label_ids: [],
    });
  });
});
