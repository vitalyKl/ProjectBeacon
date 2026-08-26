export const REVIEW_STATUSES = ["needs_review", "reviewed"] as const;

export type ReviewStatus = (typeof REVIEW_STATUSES)[number];

export type ReportSnapshot = {
  generated_at: string;
  milestones: { open: number; closed: number };
  tasks: Record<string, number>;
  ready_task_ids: string[];
  in_flight_task_ids: string[];
  review_ids: string[];
};

export type ProjectReportRecord = {
  id: string;
  projectId: string;
  title: string;
  bodyMd: string;
  snapshot: ReportSnapshot;
  createdByType: string;
  createdById: string;
  createdAt: Date;
};

export type ProjectReviewRecord = {
  id: string;
  projectId: string;
  title: string;
  bodyMd: string;
  source: string;
  sourcePath: string | null;
  status: ReviewStatus;
  createdByType: string;
  createdById: string;
  createdAt: Date;
};

export function isReviewStatus(value: string): value is ReviewStatus {
  return (REVIEW_STATUSES as readonly string[]).includes(value);
}

export type EvalFixtureSavings = {
  saved_tokens: number;
  saved_turns: number;
  better_pass: boolean;
  worse_pass: boolean;
};

export type EvalFixtureReport = {
  task: { title: string; acceptance: string };
  brief_provided: boolean;
  with_brief: { tokens_before_edit: number; turns: number; passed: boolean };
  without_brief: { tokens_before_edit: number; turns: number; passed: boolean };
  savings: EvalFixtureSavings;
};

export type EvalTotals = {
  total_saved_tokens: number;
  total_saved_turns: number;
  with_brief_passes: number;
  without_brief_passes: number;
  with_brief_avg_turns: number;
  with_brief_avg_tokens: number;
};

export type EvalMetricData = {
  schema_version: string;
  generated_at: string;
  fixtures: EvalFixtureReport[];
  totals: EvalTotals;
};

export type ProjectEvalMetricRecord = {
  id: string;
  projectId: string;
  title: string;
  snapshot: EvalMetricData;
  createdByType: string;
  createdById: string;
  createdAt: Date;
};
