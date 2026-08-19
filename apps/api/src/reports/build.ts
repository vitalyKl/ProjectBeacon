import type { MilestoneRecord, TaskRecord } from "../roadmap/types.js";
import type { ProjectReportRecord, ProjectReviewRecord, ReportSnapshot } from "./types.js";

export function emptyTaskCounts(): Record<string, number> {
  return {
    backlog: 0,
    ready: 0,
    in_progress: 0,
    blocked: 0,
    in_review: 0,
    done: 0,
    canceled: 0,
  };
}

export function buildReportSnapshot(input: {
  now: Date;
  milestones: MilestoneRecord[];
  tasks: TaskRecord[];
  reviews: ProjectReviewRecord[];
}): ReportSnapshot {
  const counts = emptyTaskCounts();
  const ready: string[] = [];
  const inFlight: string[] = [];
  for (const task of input.tasks) {
    if (task.deletedAt) {
      continue;
    }
    const current = counts[task.status];
    if (current !== undefined) {
      counts[task.status] = current + 1;
    }
    if (task.status === "ready") {
      ready.push(task.id);
    }
    if (task.status === "in_progress" || task.status === "in_review") {
      inFlight.push(task.id);
    }
  }
  return {
    generated_at: input.now.toISOString(),
    milestones: {
      open: input.milestones.filter((item) => item.status === "open").length,
      closed: input.milestones.filter((item) => item.status === "closed").length,
    },
    tasks: counts,
    ready_task_ids: ready,
    in_flight_task_ids: inFlight,
    review_ids: input.reviews.map((item) => item.id),
  };
}

export function reportMarkdown(input: {
  projectName: string;
  snapshot: ReportSnapshot;
  tasks: TaskRecord[];
  reviews: ProjectReviewRecord[];
}): string {
  const byId = new Map(input.tasks.map((task) => [task.id, task]));
  const lines = [
    `# ${input.projectName} development report`,
    "",
    `Generated ${input.snapshot.generated_at}.`,
    "",
    "## Snapshot",
    "",
    `- Open milestones: ${input.snapshot.milestones.open}`,
    `- Closed milestones: ${input.snapshot.milestones.closed}`,
    `- Ready: ${input.snapshot.tasks["ready"] ?? 0}`,
    `- In progress: ${input.snapshot.tasks["in_progress"] ?? 0}`,
    `- In review: ${input.snapshot.tasks["in_review"] ?? 0}`,
    `- Blocked: ${input.snapshot.tasks["blocked"] ?? 0}`,
    `- Done: ${input.snapshot.tasks["done"] ?? 0}`,
    "",
  ];
  if (input.snapshot.ready_task_ids.length > 0) {
    lines.push("## Ready for agents", "");
    for (const id of input.snapshot.ready_task_ids) {
      const task = byId.get(id);
      lines.push(`- ${task?.title ?? id}`);
    }
    lines.push("");
  }
  if (input.snapshot.in_flight_task_ids.length > 0) {
    lines.push("## In flight", "");
    for (const id of input.snapshot.in_flight_task_ids) {
      const task = byId.get(id);
      lines.push(`- ${task?.title ?? id} (${task?.status ?? "unknown"})`);
    }
    lines.push("");
  }
  if (input.reviews.length > 0) {
    lines.push("## Imported reviews", "");
    for (const review of input.reviews) {
      lines.push(`- ${review.title} (${review.status})`);
    }
    lines.push("");
  }
  lines.push(
    "Agents can read imported reviews and create tasks from them. Do not invent hosted clone or write_handoff.",
    "",
  );
  return lines.join("\n");
}

export function defaultReportTitle(now: Date): string {
  return `Development report ${now.toISOString().slice(0, 10)}`;
}

export function reviewTitleFromBody(title: string | undefined, body: string, path?: string): string {
  const trimmed = title?.trim();
  if (trimmed) {
    return trimmed.slice(0, 200);
  }
  const heading = /^#\s+(.+)$/m.exec(body);
  if (heading?.[1]) {
    return heading[1].trim().slice(0, 200);
  }
  if (path?.trim()) {
    return path.trim().replace(/^.*[\\/]/, "").slice(0, 200);
  }
  const first = body.trim().split(/\r?\n/, 1)[0] ?? "Imported review";
  return (first || "Imported review").slice(0, 200);
}

export function cloneReport(report: ProjectReportRecord): ProjectReportRecord {
  return {
    ...report,
    snapshot: {
      ...report.snapshot,
      milestones: { ...report.snapshot.milestones },
      tasks: { ...report.snapshot.tasks },
      ready_task_ids: [...report.snapshot.ready_task_ids],
      in_flight_task_ids: [...report.snapshot.in_flight_task_ids],
      review_ids: [...report.snapshot.review_ids],
    },
    createdAt: new Date(report.createdAt),
  };
}

export function cloneReview(review: ProjectReviewRecord): ProjectReviewRecord {
  return { ...review, createdAt: new Date(review.createdAt) };
}
