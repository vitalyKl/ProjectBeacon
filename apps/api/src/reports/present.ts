import type { ProjectReportRecord, ProjectReviewRecord } from "./types.js";

export function presentReport(report: ProjectReportRecord) {
  return {
    id: report.id,
    project_id: report.projectId,
    title: report.title,
    body_md: report.bodyMd,
    snapshot: report.snapshot,
    created_by_type: report.createdByType,
    created_by_id: report.createdById,
    created_at: report.createdAt.toISOString(),
  };
}

export function presentReview(review: ProjectReviewRecord) {
  return {
    id: review.id,
    project_id: review.projectId,
    title: review.title,
    body_md: review.bodyMd,
    source: review.source,
    source_path: review.sourcePath,
    status: review.status,
    created_by_type: review.createdByType,
    created_by_id: review.createdById,
    created_at: review.createdAt.toISOString(),
  };
}
