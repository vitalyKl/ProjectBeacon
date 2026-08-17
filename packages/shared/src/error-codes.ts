export const ERROR_CODES = [
  "unauthorized",
  "forbidden",
  "not_found",
  "version_conflict",
  "task_locked",
  "finish_work_required",
  "dependency_cycle",
  "login_taken",
  "repo_ambiguous",
  "unsupported_media",
  "rate_limited",
  "code_index_unavailable",
  "bootstrap_consumed",
  "integration_unavailable",
] as const;

export type ErrorCode = (typeof ERROR_CODES)[number];

const ERROR_CODE_SET: ReadonlySet<string> = new Set(ERROR_CODES);

export function isErrorCode(value: string): value is ErrorCode {
  return ERROR_CODE_SET.has(value);
}
