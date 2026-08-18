export const DETECT_QUEUE = "detect";

export type DetectJobData = {
  repo_id: string;
  project_id: string;
};
export function isDetectJobData(value: unknown): value is DetectJobData {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
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
}
