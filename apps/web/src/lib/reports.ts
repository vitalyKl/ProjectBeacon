import { apiFetch, fetchAllPages, parseJson, readApiError } from "./api";
import { t, type MessageKey } from "./i18n";

export type PublicReport = {
  id: string;
  project_id: string;
  title: string;
  body_md: string;
  snapshot: {
    generated_at: string;
    milestones: { open: number; closed: number };
    tasks: Record<string, number>;
    ready_task_ids: string[];
    in_flight_task_ids: string[];
    review_ids: string[];
  };
  created_by_type: string;
  created_by_id: string;
  created_at: string;
};

export type PublicReview = {
  id: string;
  project_id: string;
  title: string;
  body_md: string;
  source: string;
  source_path: string | null;
  status: "needs_review" | "reviewed";
  created_by_type: string;
  created_by_id: string;
  created_at: string;
};

export function reviewStatusLabel(status: PublicReview["status"]): string {
  return t(`reviewStatus.${status}` as MessageKey);
}

export async function fetchProjectReports(projectId: string): Promise<PublicReport[]> {
  return fetchAllPages<PublicReport>(
    `/v1/projects/${encodeURIComponent(projectId)}/reports`,
    "failed to load reports",
  );
}

export async function createProjectReport(projectId: string, title?: string): Promise<PublicReport> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/reports`, {
    method: "POST",
    body: JSON.stringify(title ? { title } : {}),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to generate report");
  }
  return parseJson<PublicReport>(res);
}

export async function fetchProjectReviews(projectId: string): Promise<PublicReview[]> {
  return fetchAllPages<PublicReview>(
    `/v1/projects/${encodeURIComponent(projectId)}/reviews`,
    "failed to load reviews",
  );
}

export async function importProjectReview(
  projectId: string,
  input: { title?: string; body_md: string; source_path?: string },
): Promise<PublicReview> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/reviews`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to import review");
  }
  return parseJson<PublicReview>(res);
}
