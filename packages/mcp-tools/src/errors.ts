import { isErrorCode, type ErrorCode } from "@beacon/shared";

export class ToolError extends Error {
  override readonly name = "ToolError";
  readonly code: ErrorCode;
  readonly status: number;
  readonly details: Record<string, unknown>;

  constructor(init: {
    code: ErrorCode;
    message: string;
    status: number;
    details?: Record<string, unknown>;
  }) {
    super(init.message);
    this.code = init.code;
    this.status = init.status;
    this.details = init.details ?? {};
  }
}

export function isToolError(error: unknown): error is ToolError {
  return error instanceof ToolError;
}

export function toolError(
  code: ErrorCode,
  status: number,
  message: string,
  details: Record<string, unknown> = {},
): ToolError {
  return new ToolError({ code, status, message, details });
}

export function invalidArguments(message = "invalid arguments"): ToolError {
  return toolError("unauthorized", 400, message, { reason: "invalid_arguments" });
}

export function integrationUnavailable(): ToolError {
  return toolError("integration_unavailable", 503, "integration unavailable");
}

export function codeIndexUnavailable(message = "code index unavailable"): ToolError {
  return toolError("code_index_unavailable", 503, message);
}

export function repoAmbiguous(): ToolError {
  return toolError("repo_ambiguous", 400, "repo_id is required");
}

type ErrorBody = {
  code: string;
  message: string;
  details?: Record<string, unknown>;
};

export function parseErrorBody(value: unknown): ErrorBody | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const error = (value as { error?: unknown }).error;
  if (error === null || typeof error !== "object" || Array.isArray(error)) {
    return undefined;
  }
  const record = error as Record<string, unknown>;
  if (typeof record["code"] !== "string" || typeof record["message"] !== "string") {
    return undefined;
  }
  const details = record["details"];
  return {
    code: record["code"],
    message: record["message"],
    details:
      details !== null && typeof details === "object" && !Array.isArray(details)
        ? (details as Record<string, unknown>)
        : {},
  };
}

export function fallbackErrorCode(status: number): ErrorCode {
  switch (status) {
    case 401:
      return "unauthorized";
    case 403:
      return "forbidden";
    case 404:
      return "not_found";
    case 409:
      return "version_conflict";
    case 415:
      return "unsupported_media";
    case 429:
      return "rate_limited";
    case 503:
      return "code_index_unavailable";
    default:
      return "unauthorized";
  }
}

export function errorFromHttp(
  status: number,
  body: unknown,
  options: { missingIndex?: boolean } = {},
): ToolError {
  const parsed = parseErrorBody(body);
  if (parsed) {
    const code = isErrorCode(parsed.code) ? parsed.code : fallbackErrorCode(status);
    return toolError(code, status, parsed.message, parsed.details ?? {});
  }

  if (
    options.missingIndex &&
    (status === 404 || status === 501 || status === 503)
  ) {
    return codeIndexUnavailable();
  }

  return toolError(fallbackErrorCode(status), status, `request failed (${status})`);
}
