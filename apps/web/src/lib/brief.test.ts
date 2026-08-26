import { describe, expect, it } from "vitest";

import type { ContextNode, ContextSection } from "./api";
import {
  isAgentStartableStatus,
  isOfferedToAgents,
  offeredTasks,
  pickDisplayBriefNode,
  sectionsWithBody,
} from "./brief";
import type { PublicTask } from "./roadmap";

function section(overrides: Partial<ContextSection> = {}): ContextSection {
  return {
    id: "goals",
    title: "Goals",
    body_md: "Keep a living brief.",
    ordinal: 0,
    ...overrides,
  };
}

function node(overrides: Partial<ContextNode> = {}): ContextNode {
  return {
    id: "node-1",
    project_id: "proj-1",
    repo_id: null,
    task_id: null,
    scope_type: "project",
    path: "",
    sections: [section()],
    source: "native",
    source_path: null,
    review_state: "reviewed",
    updated_at: "2026-08-18T12:00:00.000Z",
    updated_by: { type: "user", id: "user-1", display: "alice" },
    ...overrides,
  };
}

function task(overrides: Partial<PublicTask> = {}): PublicTask {
  return {
    id: "task-1",
    project_id: "proj-1",
    milestone_id: null,
    parent_id: null,
    title: "Ready work",
    description: "",
    status: "ready",
    priority: 2,
    type: "task",
    version: 1,
    assignee_user_id: null,
    assignee_agent_name: null,
    agent_brief: "",
    how_to_check: "",
    linked_paths: [],
    labels: [],
    github_issue_id: null,
    locked_by_session_id: null,
    lock_expires_at: null,
    deleted_at: null,
    created_at: "2026-08-18T12:00:00.000Z",
    updated_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

describe("brief helpers", () => {
  it("treats ready unlocked tasks as offered to agents", () => {
    expect(isAgentStartableStatus("ready")).toBe(true);
    expect(isAgentStartableStatus("backlog")).toBe(false);
    expect(isOfferedToAgents(task())).toBe(true);
    expect(isOfferedToAgents(task({ status: "backlog" }))).toBe(false);
    expect(
      isOfferedToAgents(
        task({
          locked_by_session_id: "sess-1",
          lock_expires_at: "2099-01-01T00:00:00.000Z",
        }),
      ),
    ).toBe(false);
    expect(isOfferedToAgents(task({ deleted_at: "2026-08-18T13:00:00.000Z" }))).toBe(false);
  });

  it("orders offered tasks by priority then recency", () => {
    const later = task({
      id: "later",
      priority: 1,
      updated_at: "2026-08-18T14:00:00.000Z",
    });
    const earlier = task({
      id: "earlier",
      priority: 1,
      updated_at: "2026-08-18T13:00:00.000Z",
    });
    const higher = task({ id: "higher", priority: 3, status: "ready" });
    const backlog = task({ id: "backlog", status: "backlog", priority: 0 });
    expect(offeredTasks([higher, backlog, earlier, later]).map((item) => item.id)).toEqual([
      "later",
      "earlier",
      "higher",
    ]);
  });

  it("drops empty sections and prefers a reviewed project-scope node", () => {
    expect(sectionsWithBody([section({ body_md: "  " }), section({ id: "style", title: "Style" })])).toEqual([
      section({ id: "style", title: "Style" }),
    ]);

    const repo = node({
      id: "repo",
      scope_type: "repo",
      updated_at: "2026-08-19T00:00:00.000Z",
    });
    const staleProject = node({
      id: "stale",
      updated_at: "2026-08-17T00:00:00.000Z",
      review_state: "needs_review",
    });
    const project = node({
      id: "project",
      updated_at: "2026-08-18T00:00:00.000Z",
    });
    expect(pickDisplayBriefNode([repo, staleProject, project])?.id).toBe("project");
    expect(pickDisplayBriefNode([repo])?.id).toBe("repo");
    expect(pickDisplayBriefNode([node({ sections: [section({ body_md: "" })] })])).toBeNull();
  });
});
