import type { ProjectEvalMetricRecord, ProjectReportRecord, ProjectReviewRecord } from "./types.js";

export interface ReportStore {
  createReport(report: ProjectReportRecord): Promise<ProjectReportRecord>;
  listReports(projectId: string): Promise<ProjectReportRecord[]>;
  findReportById(id: string): Promise<ProjectReportRecord | undefined>;
  createReview(review: ProjectReviewRecord): Promise<ProjectReviewRecord>;
  listReviews(projectId: string): Promise<ProjectReviewRecord[]>;
  findReviewById(id: string): Promise<ProjectReviewRecord | undefined>;
  createEvalMetric(record: ProjectEvalMetricRecord): Promise<ProjectEvalMetricRecord>;
  listEvalMetrics(projectId: string): Promise<ProjectEvalMetricRecord[]>;
  findEvalMetricById(id: string): Promise<ProjectEvalMetricRecord | undefined>;
}
