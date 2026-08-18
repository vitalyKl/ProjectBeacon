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
}
