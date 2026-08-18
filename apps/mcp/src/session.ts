import { parseErrorBody, toolError } from "@beacon/mcp-tools";

export type SessionContext = {
  baseUrl: string;
  token: string;
  projectId: string;
  fetch?: typeof fetch;
};

function trimSlash(value: string): string {
  return value.replace(/\/+$/, "");
}

export function resolveProjectId(
  queryProjectId: string | undefined,
  defaultProjectId: string | undefined,
): string | undefined {
  const value = queryProjectId?.trim() || defaultProjectId?.trim();
  return value && value.length > 0 ? value : undefined;
}

export async function assertProjectRead(ctx: SessionContext): Promise<void> {
  const url = `${trimSlash(ctx.baseUrl)}/v1/projects/${encodeURIComponent(ctx.projectId)}/sessions?limit=1`;
  const fetchImpl = ctx.fetch ?? fetch;
  const response = await fetchImpl(url, {
    headers: {
      authorization: `Bearer ${ctx.token}`,
      accept: "application/json",
    },
  });

  if (response.ok) {
    return;
  }

  const text = await response.text();
  let parsed: unknown;
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown;
    } catch {
      parsed = undefined;
    }
  }

  const body = parseErrorBody(parsed);
  if (response.status === 401 || body?.code === "unauthorized") {
    throw toolError("unauthorized", 401, body?.message ?? "unauthorized");
  }
  if (response.status === 403 || body?.code === "forbidden") {
    throw toolError("forbidden", 403, "initialize requires project:read");
  }
  throw toolError("forbidden", 403, "initialize requires project:read");
}
