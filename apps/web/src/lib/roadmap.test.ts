import { afterEach, describe, expect, it, vi } from "vitest";

import {
  createMilestone,
  draftFromMilestone,
  emptyMilestoneDraft,
  groupTasksByMilestone,
  milestoneWriteFromDraft,
  parseMilestoneTargetDate,
  patchMilestone,
  sortMilestones,
  type PublicMilestone,
  type PublicTask,
} from "./roadmap";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function milestone(overrides: Partial<PublicMilestone> = {}): PublicMilestone {
  return {
    id: "ms-1",
    project_id: "proj-1",
    title: "First slice",
    description: "",
    status: "open",
    target_date: null,
    sort_order: 0,
    created_at: "2026-08-18T12:00:00.000Z",
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

describe("milestone draft helpers", () => {
  it("parses empty and ISO dates, and rejects junk", () => {
    expect(parseMilestoneTargetDate("")).toBeNull();
    expect(parseMilestoneTargetDate("  ")).toBeNull();
    expect(parseMilestoneTargetDate("2026-09-01")).toBe("2026-09-01");
    expect(parseMilestoneTargetDate("soon")).toBeUndefined();
    expect(parseMilestoneTargetDate("2026-9-1")).toBeUndefined();
  });

  it("requires a title and a valid date on write", () => {
    expect(milestoneWriteFromDraft(emptyMilestoneDraft())).toEqual({
      ok: false,
      error: "title",
    });
    expect(
      milestoneWriteFromDraft({
        title: "  v1  ",
        description: "cut",
        status: "closed",
        targetDate: "soon",
      }),
    ).toEqual({ ok: false, error: "target_date" });
    expect(
      milestoneWriteFromDraft({
        title: "  v1  ",
        description: "cut",
        status: "closed",
        targetDate: "2026-09-01",
      }),
    ).toEqual({
      ok: true,
      value: {
        title: "v1",
        description: "cut",
        status: "closed",
        target_date: "2026-09-01",
      },
    });
    expect(draftFromMilestone(milestone({ target_date: "2026-10-01" })).targetDate).toBe(
      "2026-10-01",
    );
  });
});

describe("timeline grouping", () => {
  it("sorts dated milestones first, then sort_order, then title", () => {
    const later = milestone({ id: "later", title: "Later", target_date: "2026-11-01" });
    const earlier = milestone({ id: "earlier", title: "Earlier", target_date: "2026-09-01" });
    const undatedB = milestone({ id: "b", title: "Beta", sort_order: 1 });
    const undatedA = milestone({ id: "a", title: "Alpha", sort_order: 1 });
    expect(sortMilestones([later, undatedB, earlier, undatedA]).map((item) => item.id)).toEqual([
      "earlier",
      "later",
      "a",
      "b",
    ]);
  });

  it("groups tasks under their milestone and sorts unscheduled by title", () => {
    const grouped = groupTasksByMilestone([
      task({ id: "u2", title: "Zebra" }),
      task({ id: "m2", title: "Beta", milestone_id: "ms-1" }),
      task({ id: "u1", title: "Alpha" }),
      task({ id: "m1", title: "Ada", milestone_id: "ms-1" }),
    ]);
    expect(grouped.unscheduled.map((item) => item.id)).toEqual(["u1", "u2"]);
    expect(grouped.byMilestone.get("ms-1")?.map((item) => item.id)).toEqual(["m1", "m2"]);
  });
});

describe("milestone API client", () => {
  it("posts create fields and patches title status and dates", async () => {
    const created = milestone({ id: "ms-new", title: "Ship", target_date: "2026-09-01" });
    const updated = milestone({
      id: "ms-new",
      title: "Shipped",
      status: "closed",
      target_date: null,
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify(created), {
          status: 201,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(updated), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(
      createMilestone("proj-1", {
        title: "Ship",
        description: "first",
        status: "open",
        target_date: "2026-09-01",
      }),
    ).resolves.toEqual(created);
    await expect(
      patchMilestone("ms-new", {
        title: "Shipped",
        status: "closed",
        target_date: null,
      }),
    ).resolves.toEqual(updated);

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/projects/proj-1/milestones",
      expect.objectContaining({ method: "POST" }),
    );
    expect(JSON.parse((fetchMock.mock.calls[0]?.[1] as RequestInit).body as string)).toEqual({
      title: "Ship",
      description: "first",
      status: "open",
      target_date: "2026-09-01",
    });
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/v1/milestones/ms-new",
      expect.objectContaining({ method: "PATCH" }),
    );
    expect(JSON.parse((fetchMock.mock.calls[1]?.[1] as RequestInit).body as string)).toEqual({
      title: "Shipped",
      status: "closed",
      target_date: null,
    });
  });
});
