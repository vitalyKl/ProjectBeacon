import type { Scope } from "@beacon/shared";

export type TokenRecord = {
  id: string;
  projectId: string;
  name: string;
  tokenHash: Buffer;
  prefix: string;
  scopes: Scope[];
  createdBy: string | null;
  lastUsedAt: Date | null;
  expiresAt: Date | null;
  revokedAt: Date | null;
  createdAt: Date;
};

export type ApprovalStatus = "pending" | "approved" | "denied" | "expired";

export type ApprovalRecord = {
  id: string;
  projectId: string;
  sessionId: string | null;
  action: string;
  payload: Record<string, unknown>;
  status: ApprovalStatus;
  requestedAt: Date;
  resolvedAt: Date | null;
  resolvedBy: string | null;
};

export type RateBucketRecord = {
  bucketKey: string;
  windowStart: Date;
  count: number;
  bytes: bigint;
};

export const APPROVAL_STATUSES: readonly ApprovalStatus[] = [
  "pending",
  "approved",
  "denied",
  "expired",
];

export function isApprovalStatus(value: string): value is ApprovalStatus {
  return (APPROVAL_STATUSES as readonly string[]).includes(value);
}
