import { errorFromHttp, invalidArguments } from "./errors.js";
import type { InvokeContext, JsonObject } from "./types.js";

export type HttpMethod = "GET" | "POST" | "PATCH" | "PUT" | "DELETE";

export type HttpRequest = {
  method: HttpMethod;
  path: string;
  query?: Record<string, string | number | boolean | undefined | null>;
  body?: JsonObject;
  idempotencyKey?: string;
};

function trimSlash(value: string): string {
  return value.replace(/\/+$/, "");
}

export function joinUrl(baseUrl: string, path: string, query?: Record<string, string>): string {
  const origin = trimSlash(baseUrl);
  const pathname = path.startsWith("/") ? path : `/${path}`;
  const url = new URL(`${origin}${pathname}`);
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      url.searchParams.set(key, value);
    }
  }
  return url.toString();
}

export function compactQuery(
  query: Record<string, string | number | boolean | undefined | null> | undefined,
): Record<string, string> {
  const out: Record<string, string> = {};
  if (!query) {
    return out;
  }
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === "") {
      continue;
    }
    out[key] = String(value);
  }
  return out;
}

export function requireProjectId(
  args: { project_id?: string | undefined },
  ctx: InvokeContext,
): string {
  const projectId = args.project_id ?? ctx.projectId;
  if (!projectId) {
    throw invalidArguments("project_id is required");
  }
  return projectId;
}

export async function apiRequest(
  ctx: InvokeContext,
  request: HttpRequest,
  options: { missingIndex?: boolean } = {},
): Promise<unknown> {
  const query = compactQuery(request.query);
  const url = joinUrl(ctx.baseUrl, request.path, query);
  const headers: Record<string, string> = {
    authorization: `Bearer ${ctx.token}`,
    accept: "application/json",
  };
  if (request.body !== undefined) {
    headers["content-type"] = "application/json";
  }
  if (request.idempotencyKey) {
    headers["idempotency-key"] = request.idempotencyKey;
  }

  const fetchImpl = ctx.fetch ?? fetch;
  const response = await fetchImpl(url, {
    method: request.method,
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
  });

  const text = await response.text();
  let parsed: unknown = undefined;
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown;
    } catch {
      parsed = undefined;
    }
  }

  if (!response.ok) {
    throw errorFromHttp(response.status, parsed, options);
  }

  return parsed;
}
