import { Hono } from "hono";

import {
  extractJsonRpcId,
  isJsonRpcNotification,
  jsonRpcError,
  parseBearer,
  unauthorizedRpc,
} from "./auth.js";
import { handleJsonRpc } from "./protocol.js";
import { resolveProjectId, type SessionContext } from "./session.js";

export const packageName = "@beacon/mcp";

export type CreateAppOptions = {
  apiUrl?: string;
  projectId?: string;
  fetch?: typeof fetch;
};

function methodNotAllowed() {
  return jsonRpcError(null, -32000, "Method not allowed.");
}

function rpcDataCode(body: unknown): string | undefined {
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return undefined;
  }
  const data = (body as { error?: { data?: { code?: unknown } } }).error?.data;
  return typeof data?.code === "string" ? data.code : undefined;
}

export function createApp(options: CreateAppOptions = {}): Hono {
  const apiUrl = options.apiUrl ?? process.env.BEACON_API_URL ?? "http://127.0.0.1:8080";
  const defaultProjectId = options.projectId ?? process.env.BEACON_PROJECT_ID;
  const fetchImpl = options.fetch ?? fetch;
  const app = new Hono();

  app.get("/health", (c) => c.json({ status: "ok" }));

  app.get("/mcp", (c) => c.json(methodNotAllowed(), 405));
  app.delete("/mcp", (c) => c.json(methodNotAllowed(), 405));

  app.post("/mcp", async (c) => {
    const token = parseBearer(c.req.header("authorization"));
    let body: unknown;
    try {
      body = await c.req.json();
    } catch {
      return c.json(jsonRpcError(null, -32700, "parse error"), 200);
    }

    const id = extractJsonRpcId(body);
    if (!token) {
      if (isJsonRpcNotification(body)) {
        return c.body(null, 202);
      }
      return c.json(unauthorizedRpc(id), 401);
    }

    const projectId = resolveProjectId(c.req.query("project_id"), defaultProjectId);
    if (!projectId) {
      if (isJsonRpcNotification(body)) {
        return c.body(null, 202);
      }
      return c.json(unauthorizedRpc(id, "project_id is required"), 401);
    }

    const session: SessionContext = {
      baseUrl: apiUrl,
      token,
      projectId,
      fetch: fetchImpl,
    };

    const result = await handleJsonRpc(body, session);
    if (!("body" in result)) {
      return c.body(null, result.status as 202);
    }

    const code = rpcDataCode(result.body);
    if (code === "unauthorized") {
      return c.json(result.body, 401);
    }
    if (code === "forbidden") {
      return c.json(result.body, 403);
    }
    return c.json(result.body, result.status as 200);
  });

  return app;
}
