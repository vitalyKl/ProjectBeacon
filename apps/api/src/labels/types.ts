import type { LinkedPath } from "../roadmap/types.js";

export const LABEL_STATUSES = ["proposed", "active"] as const;

export type LabelStatus = (typeof LABEL_STATUSES)[number];

export type LabelRecord = {
  id: string;
  projectId: string;
  slug: string;
  name: string;
  description: string;
  color: string | null;
  status: LabelStatus;
  createdAt: Date;
  paths: LinkedPath[];
};

export type LabelPatch = {
  name?: string;
  slug?: string;
  description?: string;
  color?: string | null;
  status?: LabelStatus;
  paths?: LinkedPath[];
};

export function isLabelStatus(value: string): value is LabelStatus {
  return (LABEL_STATUSES as readonly string[]).includes(value);
}
