import { cloneReport, cloneReview } from "../../reports/build.js";
import type { ProjectReportRecord, ProjectReviewRecord } from "../../reports/types.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryReports<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryReports extends Base {
  async createReport(report: ProjectReportRecord): Promise<ProjectReportRecord> {
    this.reports.set(report.id, cloneReport(report));
    return cloneReport(report);
  }

  async listReports(projectId: string): Promise<ProjectReportRecord[]> {
    return [...this.reports.values()]
      .filter((item) => item.projectId === projectId)
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id))
      .map(cloneReport);
  }

  async findReportById(id: string): Promise<ProjectReportRecord | undefined> {
    const report = this.reports.get(id);
    return report ? cloneReport(report) : undefined;
  }

  async createReview(review: ProjectReviewRecord): Promise<ProjectReviewRecord> {
    this.reviews.set(review.id, cloneReview(review));
    return cloneReview(review);
  }

  async listReviews(projectId: string): Promise<ProjectReviewRecord[]> {
    return [...this.reviews.values()]
      .filter((item) => item.projectId === projectId)
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id))
      .map(cloneReview);
  }

  async findReviewById(id: string): Promise<ProjectReviewRecord | undefined> {
    const review = this.reviews.get(id);
    return review ? cloneReview(review) : undefined;
  }
  };
}
