import { describe, expect, it } from "vitest";

import { applyTaskPatch } from "./patch.js";
import type { TaskRecord } from "./types.js";

const NOW = new Date("2026-01-01T00:00:00.000Z");

function task(overrides: Partial<TaskRecord> = {}): TaskRecord {
  return {
    id: "018f1e2c-3d4e-7000-8000-000000000001",
    projectId: "018f1e2c-3d4e-7000-8000-000000000010",
    milestoneId: null,
    parentId: null,
    title: "Fix login",
    description: "users cannot sign in",
    status: "ready",
    priority: 0,
    type: "bug",
    version: 1,
    assigneeUserId: null,
    assigneeAgentName: null,
    agentBrief: "",
    howToCheck: "",
    linkedPaths: [{ repo_id: "repo-1", path: "apps/api" }],
    githubIssueId: null,
    lockedBySessionId: null,
    lockExpiresAt: null,
    deletedAt: null,
    createdAt: NOW,
    updatedAt: NOW,
    ...overrides,
  };
}

describe("applyTaskPatch", () => {
  it("writes githubIssueId and leaves omitted fields", () => {
    const current = task();
    const next = applyTaskPatch(current, { githubIssueId: 9001n });
    expect(next.githubIssueId).toBe(9001n);
    expect(next.title).toBe("Fix login");
    expect(next.version).toBe(1);
  });

  it("clears githubIssueId when the patch is null", () => {
    const next = applyTaskPatch(task({ githubIssueId: 9001n }), { githubIssueId: null });
    expect(next.githubIssueId).toBeNull();
  });

  it("clones linkedPaths so the next record does not share the array", () => {
    const current = task();
    const paths = [{ repo_id: "repo-2", path: "apps/web" }];
    const next = applyTaskPatch(current, { linkedPaths: paths });
    paths[0]!.path = "mutated";
    current.linkedPaths[0]!.path = "also-mutated";
    expect(next.linkedPaths).toEqual([{ repo_id: "repo-2", path: "apps/web" }]);
  });
});
