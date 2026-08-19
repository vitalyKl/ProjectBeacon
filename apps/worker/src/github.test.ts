import { describe, expect, it } from "vitest";

import type { GithubIssueView, WorkerApi } from "./client.js";
import { runGithubImportJob, runGithubInvalidateJob } from "./github.js";

function mockApi(overrides: Partial<WorkerApi> = {}): WorkerApi & {
  imported: Array<{ repoId: string; issues: unknown[] }>;
  invalidations: Array<{ repoId: string; body: unknown }>;
} {
  const imported: Array<{ repoId: string; issues: unknown[] }> = [];
  const invalidations: Array<{ repoId: string; body: unknown }> = [];
  const issues: GithubIssueView[] = [
    {
      id: "9001",
      number: 12,
      title: "Broken login",
      body: "users cannot sign in",
      state: "open",
      html_url: "https://github.com/acme/demo/issues/12",
    },
  ];
  return {
    imported,
    invalidations,
    async getRepo() {
      return {
        id: "repo-1",
        project_id: "proj-1",
        provider: "github",
        remote_url: null,
        default_branch: "main",
        installation_id: null,
        local_root_hint: null,
        index_mode: "sidecar",
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
    async importContext() {
      return {};
    },
    async listMilestones() {
      return [];
    },
    async createMilestone() {
      return { id: "ms-1" };
    },
    async listTasks() {
      return [];
    },
    async createTask() {
      return { id: "task-1" };
    },
    async listLabels() {
      return [];
    },
    async patchLabel(labelId, body) {
      return {
        id: labelId,
        slug: "api",
        name: "API",
        status: "active",
        paths: body.paths ?? [],
      };
    },
    async listGithubIssues() {
      return issues;
    },
    async getGithubIssue(_repoId, issueNumber) {
      return issues.find((item) => item.number === issueNumber);
    },
    async upsertImportedIssues(repoId, next) {
      imported.push({ repoId, issues: next });
      return { count: next.length };
    },
    async recordGithubInvalidation(repoId, body) {
      invalidations.push({ repoId, body });
      return { recorded: true };
    },
    ...overrides,
  };
}

describe("runGithubImportJob", () => {
  it("upserts listed issues through /v1", async () => {
    const api = mockApi();
    const result = await runGithubImportJob({ repo_id: "repo-1", project_id: "proj-1" }, { api });
    expect(result.imported).toBe(1);
    expect(api.imported[0]?.issues).toEqual([
      expect.objectContaining({ github_issue_id: "9001", number: 12, title: "Broken login" }),
    ]);
  });

  it("imports a single issue number without rewriting GitHub", async () => {
    const api = mockApi();
    const result = await runGithubImportJob(
      { repo_id: "repo-1", project_id: "proj-1", issue_number: 12 },
      { api },
    );
    expect(result.imported).toBe(1);
    expect(api.imported).toHaveLength(1);
  });
});

describe("runGithubInvalidateJob", () => {
  it("records invalidation through /v1", async () => {
    const api = mockApi();
    await runGithubInvalidateJob(
      { repo_id: "repo-1", project_id: "proj-1", ref: "refs/heads/main" },
      { api },
    );
    expect(api.invalidations).toEqual([
      { repoId: "repo-1", body: { ref: "refs/heads/main", before: undefined, after: undefined } },
    ]);
  });
});
