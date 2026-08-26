import { describe, expect, it } from "vitest";

import type { MilestoneRecord, TaskRecord } from "../roadmap/types.js";
import { buildReportSnapshot, reportMarkdown, reviewTitleFromBody } from "./build.js";
import type { ProjectEvalMetricRecord, ProjectReviewRecord } from "./types.js";

const NOW = new Date("2026-08-19T12:00:00.000Z");

function task(overrides: Partial<TaskRecord>): TaskRecord {
  return {
    id: "018f1e2c-3d4e-7000-8000-000000000001",
    projectId: "018f1e2c-3d4e-7000-8000-000000000010",
    milestoneId: null,
    parentId: null,
    title: "Ready work",
    description: "",
    status: "ready",
    priority: 0,
    type: "task",
    version: 1,
    assigneeUserId: null,
    assigneeAgentName: null,
    agentBrief: "",
    howToCheck: "",
    linkedPaths: [],
    githubIssueId: null,
    lockedBySessionId: null,
    lockExpiresAt: null,
    deletedAt: null,
    createdAt: NOW,
    updatedAt: NOW,
    ...overrides,
  };
}

describe("report snapshot", () => {
  it("counts live tasks and lists ready and in-flight ids", () => {
    const milestones: MilestoneRecord[] = [
      {
        id: "m1",
        projectId: "p",
        title: "v1",
        description: "",
        status: "open",
        targetDate: null,
        sortOrder: 0,
        createdAt: NOW,
      },
    ];
    const snapshot = buildReportSnapshot({
      now: NOW,
      milestones,
      tasks: [
        task({ id: "ready-1", status: "ready", title: "Ready" }),
        task({ id: "progress-1", status: "in_progress", title: "Doing" }),
        task({ id: "gone", status: "done", deletedAt: NOW, title: "Deleted" }),
      ],
      reviews: [
        {
          id: "rev-1",
          projectId: "p",
          title: "UX pass",
          bodyMd: "Check the board.",
          source: "imported",
          sourcePath: "review.md",
          status: "needs_review",
          createdByType: "user",
          createdById: "u",
          createdAt: NOW,
        } satisfies ProjectReviewRecord,
      ],
    });
    expect(snapshot.tasks["ready"]).toBe(1);
    expect(snapshot.tasks["in_progress"]).toBe(1);
    expect(snapshot.tasks["done"]).toBe(0);
    expect(snapshot.ready_task_ids).toEqual(["ready-1"]);
    expect(snapshot.in_flight_task_ids).toEqual(["progress-1"]);
    expect(snapshot.review_ids).toEqual(["rev-1"]);
    const markdown = reportMarkdown({
      projectName: "Beacon",
      snapshot,
      tasks: [task({ id: "ready-1", title: "Ready" }), task({ id: "progress-1", status: "in_progress", title: "Doing" })],
      reviews: [
        {
          id: "rev-1",
          projectId: "p",
          title: "UX pass",
          bodyMd: "Check the board.",
          source: "imported",
          sourcePath: "review.md",
          status: "needs_review",
          createdByType: "user",
          createdById: "u",
          createdAt: NOW,
        },
      ],
    });
    expect(markdown).toContain("# Beacon development report");
    expect(markdown).toContain("- Ready");
    expect(markdown).toContain("UX pass");
  });

  it("picks a review title from heading, path, or first line", () => {
    expect(reviewTitleFromBody("Named", "# Ignored", "file.md")).toBe("Named");
    expect(reviewTitleFromBody(undefined, "# From heading\nBody", "file.md")).toBe("From heading");
    expect(reviewTitleFromBody(undefined, "plain", "docs/review.md")).toBe("review.md");
  });
});

describe("eval metrics", () => {
  it("builds an eval metric with fixture savings", () => {
    const snapshot: ProjectEvalMetricRecord["snapshot"] = {
      schema_version: "1",
      generated_at: "2026-08-26T12:00:00.000Z",
      fixtures: [
        {
          task: { title: "Update CLI help", acceptance: "Help text shows correct flags" },
          brief_provided: true,
          with_brief: { tokens_before_edit: 2000, turns: 5, passed: true },
          without_brief: { tokens_before_edit: 5600, turns: 10, passed: false },
          savings: { saved_tokens: 3600, saved_turns: 5, better_pass: true, worse_pass: false },
        },
      ],
      totals: {
        total_saved_tokens: 3600,
        total_saved_turns: 5,
        with_brief_passes: 1,
        without_brief_passes: 0,
        with_brief_avg_turns: 5,
        with_brief_avg_tokens: 2000,
      },
    };
    expect(snapshot.fixtures).toHaveLength(1);
    expect(snapshot.totals.total_saved_tokens).toBe(3600);
    expect(snapshot.totals.total_saved_turns).toBe(5);
    expect(snapshot.fixtures[0]!.savings.saved_tokens).toBe(3600);
    expect(snapshot.fixtures[0]!.savings.better_pass).toBe(true);
  });

  it("handles empty eval metrics", () => {
    const snapshot: ProjectEvalMetricRecord["snapshot"] = {
      schema_version: "1",
      generated_at: "2026-08-26T12:00:00.000Z",
      fixtures: [],
      totals: {
        total_saved_tokens: 0,
        total_saved_turns: 0,
        with_brief_passes: 0,
        without_brief_passes: 0,
        with_brief_avg_turns: 0,
        with_brief_avg_tokens: 0,
      },
    };
    expect(snapshot.fixtures).toHaveLength(0);
    expect(snapshot.totals.total_saved_tokens).toBe(0);
  });
});
