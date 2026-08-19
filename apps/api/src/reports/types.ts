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
