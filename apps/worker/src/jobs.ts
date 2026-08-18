export const DETECT_QUEUE = "detect";
export const GITHUB_IMPORT_QUEUE = "github_import";
export const GITHUB_INVALIDATE_QUEUE = "github_invalidate";

export type DetectJobData = {
  repo_id: string;
  project_id: string;
};

export type GithubImportJobData = {
  repo_id: string;
  project_id: string;
  issue_number?: number;
};

export type GithubInvalidateJobData = {
  repo_id: string;
  project_id: string;
  ref?: string;
  before?: string;
  after?: string;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function isDetectJobData(value: unknown): value is DetectJobData {
  if (!isRecord(value)) {
    return false;
  }
  const record = value as Record<string, unknown>;
  return typeof record["repo_id"] === "string" && typeof record["project_id"] === "string";
import type { ExpireLocksResult, RetentionResult, WorkerApi } from "./api.js";
export const RETENTION_QUEUE = "retention";
export const EXPIRE_LOCKS_QUEUE = "expire-locks";
export const RETENTION_CRON = "0 3 * * *";
export const EXPIRE_LOCKS_CRON = "*/15 * * * *";
export async function runRetentionJob(api: WorkerApi): Promise<RetentionResult> {
  return api.runRetention();
}
export async function runExpireLocksJob(api: WorkerApi): Promise<ExpireLocksResult> {
  return api.expireLocks();
  return typeof value["repo_id"] === "string" && typeof value["project_id"] === "string";

export function isGithubImportJobData(value: unknown): value is GithubImportJobData {
  if (!isRecord(value)) {
    return false;
  }
  if (typeof value["repo_id"] !== "string" || typeof value["project_id"] !== "string") {
  const issueNumber = value["issue_number"];
  return (
    issueNumber === undefined ||
    (typeof issueNumber === "number" && Number.isInteger(issueNumber) && issueNumber > 0)
  );
export function isGithubInvalidateJobData(value: unknown): value is GithubInvalidateJobData {
  return isDetectJobData(value);
}
