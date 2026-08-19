import { apiFetch, fetchAllPages, parseJson, readApiError } from "./api";
import { t, type MessageKey } from "./i18n";
import type { LinkedPath, PublicTask } from "./roadmap";

export const LABEL_STATUSES = ["proposed", "active"] as const;

export type LabelStatus = (typeof LABEL_STATUSES)[number];

export type PublicLabelSummary = {
  id: string;
  slug: string;
  name: string;
  color: string | null;
  status: LabelStatus;
};

export type PublicLabel = PublicLabelSummary & {
  project_id: string;
  description: string;
  created_at: string;
  paths: LinkedPath[];
};

export type CreateLabelInput = {
  name: string;
  slug?: string;
  description?: string;
  color?: string | null;
  status?: LabelStatus;
  paths?: LinkedPath[];
};

export type PatchLabelInput = {
  name?: string;
  slug?: string;
  description?: string;
  color?: string | null;
  status?: LabelStatus;
  paths?: LinkedPath[];
};

export async function fetchProjectLabels(projectId: string): Promise<PublicLabel[]> {
  return fetchAllPages<PublicLabel>(
    `/v1/projects/${encodeURIComponent(projectId)}/labels`,
    "failed to load labels",
  );
}

export async function createLabel(
  projectId: string,
  input: CreateLabelInput,
): Promise<PublicLabel> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/labels`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create label");
  }
  return parseJson<PublicLabel>(res);
}

export async function patchLabel(labelId: string, input: PatchLabelInput): Promise<PublicLabel> {
  const res = await apiFetch(`/v1/labels/${encodeURIComponent(labelId)}`, {
    method: "PATCH",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update label");
  }
  return parseJson<PublicLabel>(res);
}

export async function setTaskLabels(taskId: string, labelIds: string[]): Promise<PublicTask> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(taskId)}/labels`, {
    method: "PUT",
    body: JSON.stringify({ label_ids: labelIds }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update task labels");
  }
  return parseJson<PublicTask>(res);
}

export function isLabelStatus(value: string): value is LabelStatus {
  return (LABEL_STATUSES as readonly string[]).includes(value);
}

export function labelStatusLabel(status: LabelStatus): string {
  return t(`labelStatus.${status}` as MessageKey);
}

const LABEL_STATUS_ORDER: Record<LabelStatus, number> = {
  proposed: 0,
  active: 1,
};

export function sortLabels(items: PublicLabel[]): PublicLabel[] {
  return [...items].sort((left, right) => {
    const statusDelta = LABEL_STATUS_ORDER[left.status] - LABEL_STATUS_ORDER[right.status];
    if (statusDelta !== 0) {
      return statusDelta;
    }
    return left.name.localeCompare(right.name) || left.id.localeCompare(right.id);
  });
}

export function formatLabelPaths(label: Pick<PublicLabel, "paths">): string {
  if (label.paths.length === 0) {
    return t("labels.noPrefixes");
  }
  return label.paths.map((item) => item.path).join(", ");
}

export function toggleLabelId(selected: string[], labelId: string): string[] {
  return selected.includes(labelId)
    ? selected.filter((id) => id !== labelId)
    : [...selected, labelId];
}

export function activeCatalogLabels(items: PublicLabel[]): PublicLabel[] {
  return items.filter((item) => item.status === "active");
}

export function upsertLabelPath(paths: LinkedPath[], next: LinkedPath): LinkedPath[] {
  const path = next.path.trim().replace(/^\/+|\/+$/g, "");
  if (!path || !next.repo_id) {
    return paths;
  }
  if (paths.some((item) => item.repo_id === next.repo_id && item.path === path)) {
    return paths;
  }
  return [...paths, { repo_id: next.repo_id, path }];
}

export function removeLabelPath(paths: LinkedPath[], repoId: string, path: string): LinkedPath[] {
  return paths.filter((item) => !(item.repo_id === repoId && item.path === path));
}
