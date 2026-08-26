import { projectEvalMetrics, projectReports, projectReviews } from "@beacon/db";
import { desc, eq } from "drizzle-orm";
import type { ProjectEvalMetricRecord, ProjectReportRecord, ProjectReviewRecord } from "../../reports/types.js";
import { toEvalMetric, toReport, toReview } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { ReportStore } from "../../reports/store.js";

export function withDbReports<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<ReportStore> {
  return class DbReports extends Base {
  async createReport(report: ProjectReportRecord): Promise<ProjectReportRecord> {
    const [row] = await this.db
      .insert(projectReports)
      .values({
        id: report.id,
        projectId: report.projectId,
        title: report.title,
        bodyMd: report.bodyMd,
        snapshot: report.snapshot,
        createdByType: report.createdByType,
        createdById: report.createdById,
        createdAt: report.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert report returned no row");
    }
    return toReport(row);
  }

  async listReports(projectId: string): Promise<ProjectReportRecord[]> {
    const rows = await this.db
      .select()
      .from(projectReports)
      .where(eq(projectReports.projectId, projectId))
      .orderBy(desc(projectReports.createdAt), desc(projectReports.id));
    return rows.map(toReport);
  }

  async findReportById(id: string): Promise<ProjectReportRecord | undefined> {
    const [row] = await this.db.select().from(projectReports).where(eq(projectReports.id, id)).limit(1);
    return row ? toReport(row) : undefined;
  }

  async createReview(review: ProjectReviewRecord): Promise<ProjectReviewRecord> {
    const [row] = await this.db
      .insert(projectReviews)
      .values({
        id: review.id,
        projectId: review.projectId,
        title: review.title,
        bodyMd: review.bodyMd,
        source: review.source,
        sourcePath: review.sourcePath,
        status: review.status,
        createdByType: review.createdByType,
        createdById: review.createdById,
        createdAt: review.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert review returned no row");
    }
    return toReview(row);
  }

  async listReviews(projectId: string): Promise<ProjectReviewRecord[]> {
    const rows = await this.db
      .select()
      .from(projectReviews)
      .where(eq(projectReviews.projectId, projectId))
      .orderBy(desc(projectReviews.createdAt), desc(projectReviews.id));
    return rows.map(toReview);
  }

  async findReviewById(id: string): Promise<ProjectReviewRecord | undefined> {
    const [row] = await this.db.select().from(projectReviews).where(eq(projectReviews.id, id)).limit(1);
    return row ? toReview(row) : undefined;
  }

  async createEvalMetric(record: ProjectEvalMetricRecord): Promise<ProjectEvalMetricRecord> {
    const [row] = await this.db
      .insert(projectEvalMetrics)
      .values({
        id: record.id,
        projectId: record.projectId,
        title: record.title,
        snapshot: record.snapshot,
        createdByType: record.createdByType,
        createdById: record.createdById,
        createdAt: record.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert eval metric returned no row");
    }
    return toEvalMetric(row);
  }

  async listEvalMetrics(projectId: string): Promise<ProjectEvalMetricRecord[]> {
    const rows = await this.db
      .select()
      .from(projectEvalMetrics)
      .where(eq(projectEvalMetrics.projectId, projectId))
      .orderBy(desc(projectEvalMetrics.createdAt), desc(projectEvalMetrics.id));
    return rows.map(toEvalMetric);
  }

  async findEvalMetricById(id: string): Promise<ProjectEvalMetricRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(projectEvalMetrics)
      .where(eq(projectEvalMetrics.id, id))
      .limit(1);
    return row ? toEvalMetric(row) : undefined;
  }
  } as TBase & Ctor<ReportStore>;
}
