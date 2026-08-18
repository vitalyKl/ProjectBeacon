import { parseErrorBody } from "@beacon/mcp-tools";

export type ApiError = {
  status: number;
  code?: string;
  message: string;
};

export type ProjectSnapshot = {
  id: string;
  name?: string;
  slug?: string;
};

export type FetchLike = typeof fetch;

function joinUrl(baseUrl: string, path: string): string {
  const origin = baseUrl.replace(/\/+$/, "");
  const pathname = path.startsWith("/") ? path : `/${path}`;
  return `${origin}${pathname}`;
}

export async function apiGet(
  baseUrl: string,
  token: string,
  path: string,
  fetchImpl: FetchLike = fetch,
): Promise<{ status: number; body: unknown }> {
  const response = await fetchImpl(joinUrl(baseUrl, path), {
    method: "GET",
    headers: {
      authorization: `Bearer ${token}`,
      accept: "application/json",
    },
  });
  const text = await response.text();
  let parsed: unknown;
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown;
    } catch {
      parsed = undefined;
    }
  }
  return { status: response.status, body: parsed };
}

export function describeApiFailure(status: number, body: unknown): ApiError {
  const parsed = parseErrorBody(body);
  if (parsed) {
    return { status, code: parsed.code, message: parsed.message };
  }
  return { status, message: `request failed (${status})` };
}

export function isForbidden(error: ApiError): boolean {
  return error.status === 403 || error.code === "forbidden";
}

export function isUnauthorized(error: ApiError): boolean {
  return error.status === 401 || error.code === "unauthorized";
}

export function isNotFound(error: ApiError): boolean {
  return error.status === 404 || error.code === "not_found";
}

export function asProjectSnapshot(value: unknown): ProjectSnapshot | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  if (typeof record["id"] !== "string") {
    return undefined;
  }
  return {
    id: record["id"],
    name: typeof record["name"] === "string" ? record["name"] : undefined,
    slug: typeof record["slug"] === "string" ? record["slug"] : undefined,
  };
}

export async function fetchProjectRead(
  baseUrl: string,
  token: string,
  projectId: string,
  fetchImpl: FetchLike = fetch,
): Promise<{ ok: true; project?: ProjectSnapshot } | { ok: false; error: ApiError }> {
  const result = await apiGet(baseUrl, token, `/v1/projects/${projectId}`, fetchImpl);
  if (result.status >= 200 && result.status < 300) {
    return { ok: true, project: asProjectSnapshot(result.body) };
  }
  return { ok: false, error: describeApiFailure(result.status, result.body) };
}
