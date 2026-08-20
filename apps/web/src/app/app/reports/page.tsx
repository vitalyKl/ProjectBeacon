"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";

import { ApiError } from "@/lib/api";
import {
  createProjectReport,
  fetchProjectReports,
  fetchProjectReviews,
  importProjectReview,
  reviewStatusLabel,
  type PublicReport,
  type PublicReview,
} from "@/lib/reports";
import { FIELD_INPUT_CLASS, FIELD_TEXTAREA_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { Button } from "@/lib/ui/button";
import { EmptyState } from "@/lib/ui/empty-state";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";
import { useT } from "@/lib/use-locale";

import { MarkdownView } from "../markdown-view";
import { useSelectedProject } from "../project-context";

export default function ReportsPage() {
  const { project } = useSelectedProject();
  const t = useT();
  const [reports, setReports] = useState<PublicReport[]>([]);
  const [reviews, setReviews] = useState<PublicReview[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [reviewTitle, setReviewTitle] = useState("");
  const [reviewBody, setReviewBody] = useState("");
  const [openReportId, setOpenReportId] = useState<string | null>(null);
  const [openReviewId, setOpenReviewId] = useState<string | null>(null);
  const projectId = project?.id ?? null;

  const reload = useCallback(async () => {
    if (!projectId) {
      return;
    }
    try {
      const [nextReports, nextReviews] = await Promise.all([
        fetchProjectReports(projectId),
        fetchProjectReviews(projectId),
      ]);
      setReports(nextReports);
      setReviews(nextReviews);
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("reports.loadFailed"));
    } finally {
      setLoading(false);
    }
  }, [projectId, t]);

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setReports([]);
        setReviews([]);
        setLoading(false);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => window.clearTimeout(id);
  }, [projectId, reload]);

  async function onGenerate() {
    if (!projectId) {
      return;
    }
    setGenerating(true);
    try {
      const created = await createProjectReport(projectId);
      setReports((current) => [created, ...current]);
      setOpenReportId(created.id);
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("reports.generateFailed"));
    } finally {
      setGenerating(false);
    }
  }

  async function onImport(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!projectId || !reviewBody.trim()) {
      return;
    }
    setImporting(true);
    try {
      const created = await importProjectReview(projectId, {
        title: reviewTitle.trim() || undefined,
        body_md: reviewBody,
      });
      setReviews((current) => [created, ...current]);
      setReviewTitle("");
      setReviewBody("");
      setOpenReviewId(created.id);
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("reports.importFailed"));
    } finally {
      setImporting(false);
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <PageHeader title={t("reports.title")} description={t("common.selectProject")} />
      </section>
    );
  }

  return (
    <section className="space-y-6">
      <PageHeader
        title={t("reports.title")}
        description={t("reports.intro")}
        actions={
          <Button type="button" disabled={generating} onClick={() => void onGenerate()}>
            {generating ? t("reports.generating") : t("reports.generate")}
          </Button>
        }
      />
      {error ? <Banner tone="danger">{error}</Banner> : null}
      {loading ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel className="space-y-3">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("reports.snapshots")}</h2>
          {reports.length === 0 ? (
            <EmptyState description={t("reports.emptyReports")} />
          ) : (
            <ul className="space-y-3">
              {reports.map((report) => (
                <li key={report.id} className="space-y-2">
                  <button
                    className="text-left text-sm font-medium underline-offset-2 hover:underline"
                    type="button"
                    onClick={() => setOpenReportId((current) => (current === report.id ? null : report.id))}
                  >
                    {report.title}
                  </button>
                  <p className="text-xs text-muted">{new Date(report.created_at).toLocaleString()}</p>
                  {openReportId === report.id ? <MarkdownView source={report.body_md} /> : null}
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel className="space-y-3">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("reports.reviews")}</h2>
          <p className="text-sm text-muted">{t("reports.reviewsIntro")}</p>
          <form className="space-y-2" onSubmit={(event) => void onImport(event)}>
            <input
              className={`${FIELD_INPUT_CLASS} w-full`}
              value={reviewTitle}
              onChange={(event) => setReviewTitle(event.target.value)}
              placeholder={t("reports.reviewTitle")}
              maxLength={200}
            />
            <textarea
              className={`${FIELD_TEXTAREA_CLASS} min-h-28 w-full font-mono`}
              value={reviewBody}
              onChange={(event) => setReviewBody(event.target.value)}
              placeholder={t("reports.reviewBody")}
              required
              maxLength={32000}
            />
            <Button variant="secondary" type="submit" disabled={importing}>
              {importing ? t("reports.importing") : t("reports.import")}
            </Button>
          </form>
          {reviews.length === 0 ? (
            <EmptyState description={t("reports.emptyReviews")} />
          ) : (
            <ul className="space-y-3">
              {reviews.map((review) => (
                <li key={review.id} className="space-y-2">
                  <button
                    className="text-left text-sm font-medium underline-offset-2 hover:underline"
                    type="button"
                    onClick={() => setOpenReviewId((current) => (current === review.id ? null : review.id))}
                  >
                    {review.title}
                  </button>
                  <p className="text-xs text-muted">
                    {reviewStatusLabel(review.status)} · {new Date(review.created_at).toLocaleString()}
                  </p>
                  {openReviewId === review.id ? <MarkdownView source={review.body_md} /> : null}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </section>
  );
}
