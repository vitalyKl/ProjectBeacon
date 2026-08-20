import type { ProjectReportRecord, ProjectReviewRecord } from "./types.js";

export interface ReportStore {
  createReport(report: ProjectReportRecord): Promise<ProjectReportRecord>;
  listReports(projectId: string): Promise<ProjectReportRecord[]>;
  findReportById(id: string): Promise<ProjectReportRecord | undefined>;
  createReview(review: ProjectReviewRecord): Promise<ProjectReviewRecord>;
  listReviews(projectId: string): Promise<ProjectReviewRecord[]>;
  findReviewById(id: string): Promise<ProjectReviewRecord | undefined>;
}
