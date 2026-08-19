import { describe, expect, it } from "vitest";

import type { MilestoneRecord, TaskRecord } from "../roadmap/types.js";
import { buildReportSnapshot, reportMarkdown, reviewTitleFromBody } from "./build.js";
import type { ProjectReviewRecord } from "./types.js";

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
