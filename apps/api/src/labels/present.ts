import type { LabelRecord } from "./types.js";

export function presentLabel(label: LabelRecord) {
  return {
    id: label.id,
    project_id: label.projectId,
    slug: label.slug,
    name: label.name,
    description: label.description,
    color: label.color,
    status: label.status,
    created_at: label.createdAt.toISOString(),
    paths: label.paths.map((path) => ({ repo_id: path.repo_id, path: path.path })),
  };
}

export function presentLabelSummary(label: LabelRecord) {
  return {
    id: label.id,
    slug: label.slug,
    name: label.name,
    color: label.color,
    status: label.status,
  };
}
