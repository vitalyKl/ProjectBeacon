import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import { HTTP_MCP_INSTRUCTIONS } from "./protocol.js";

const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";
const TASK_ID = "01934567-89ab-7cde-89ab-0123456789ab";
const TOKEN = "bcn_testtoken";

type RecordedCall = {
  method: string;
  url: URL;
  headers: Record<string, string>;
  body: unknown;
};

function headerMap(headers: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (!headers) {
    return out;
  }
  if (headers instanceof Headers) {
    headers.forEach((value, key) => {
      out[key.toLowerCase()] = value;
    });
    return out;
  }
  for (const [key, value] of Object.entries(headers as Record<string, string>)) {
    out[key.toLowerCase()] = value;
  }
  return out;
}

function mockFetch(handler: (call: RecordedCall) => { status?: number; body?: unknown }) {
  const calls: RecordedCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = new URL(
      typeof input === "string" ? input : input instanceof URL ? input.href : input.url,
    );
    const call: RecordedCall = {
      method: (init?.method ?? "GET").toUpperCase(),
      url,
      headers: headerMap(init?.headers),
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
    };
    calls.push(call);
    const result = handler(call);
    return new Response(JSON.stringify(result.body ?? { ok: true }), {
      status: result.status ?? 200,
      headers: { "content-type": "application/json" },
    });
  };
  return { fetchImpl, calls };
}

function appWith(fetchImpl: typeof fetch) {
  return createApp({
    apiUrl: "https://beacon.test",
    projectId: PROJECT_ID,
    fetch: fetchImpl,
  });
}

async function rpc(
  app: ReturnType<typeof createApp>,
  method: string,
  params: unknown = {},
  headers: Record<string, string> = { authorization: `Bearer ${TOKEN}` },
  extra: { id?: string | number | null; notification?: boolean } = {},
) {
  const body: Record<string, unknown> = {
    jsonrpc: "2.0",
    method,
    params,
  };
  if (!extra.notification) {
    body["id"] = extra.id === undefined ? 1 : extra.id;
  }
  return app.request("/mcp", {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

describe("GET /health", () => {
  it("returns 200 without calling the API", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    const app = appWith(fetchImpl);
    const res = await app.request("/health");
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ status: "ok" });
    expect(calls).toHaveLength(0);
  });
});

describe("POST /mcp", () => {
  it("rejects missing bearer tokens", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    const app = appWith(fetchImpl);
    const res = await rpc(app, "initialize", { protocolVersion: "2025-03-26" }, {});
    expect(res.status).toBe(401);
    expect(await res.json()).toMatchObject({
      jsonrpc: "2.0",
      error: { data: { code: "unauthorized" } },
    });
    expect(calls).toHaveLength(0);
  });

  it("fails initialize without project:read", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({
      status: 403,
      body: { error: { code: "forbidden", message: "insufficient token scope" } },
    }));
    const app = appWith(fetchImpl);
    const res = await rpc(app, "initialize", { protocolVersion: "2025-03-26" });
    expect(res.status).toBe(403);
    expect(await res.json()).toMatchObject({
      jsonrpc: "2.0",
      error: { data: { code: "forbidden" } },
    });
    expect(calls[0]?.method).toBe("GET");
    expect(calls[0]?.url.pathname).toBe(`/v1/projects/${PROJECT_ID}/sessions`);
    expect(calls[0]?.headers.authorization).toBe(`Bearer ${TOKEN}`);
  });

  it("hides a missing project and surfaces API 5xx as an internal error", async () => {
    const missing = mockFetch(() => ({
      status: 404,
      body: { error: { code: "not_found", message: "project not found" } },
    }));
    const missingRes = await rpc(appWith(missing.fetchImpl), "initialize", {
      protocolVersion: "2025-03-26",
    });
    expect(missingRes.status).toBe(403);
    expect(await missingRes.json()).toMatchObject({
      jsonrpc: "2.0",
      error: { message: "project not available", data: { code: "forbidden" } },
    });

    const down = mockFetch(() => ({ status: 503, body: { error: { message: "unavailable" } } }));
    const downRes = await rpc(appWith(down.fetchImpl), "initialize", {
      protocolVersion: "2025-03-26",
    });
    expect(downRes.status).toBe(200);
    expect(await downRes.json()).toMatchObject({
      jsonrpc: "2.0",
      error: { code: -32603, data: { code: "internal_error" } },
    });
  });

  it("rejects a missing project_id as validation, not unauthorized", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    const app = createApp({ apiUrl: "https://beacon.test", fetch: fetchImpl });
    const res = await rpc(app, "initialize", { protocolVersion: "2025-03-26" });
    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({
      jsonrpc: "2.0",
      error: { code: -32602, message: "project_id is required", data: { code: "invalid_request" } },
    });
    expect(calls).toHaveLength(0);
  });

  it("initializes with control-plane instructions", async () => {
    const { fetchImpl } = mockFetch((call) => {
      if (call.url.pathname === `/v1/projects/${PROJECT_ID}/sessions`) {
        return { body: { items: [], next_cursor: null } };
      }
      return { status: 500, body: { error: { code: "unauthorized", message: "unexpected" } } };
    });
    const app = appWith(fetchImpl);
    const res = await rpc(app, "initialize", { protocolVersion: "2025-03-26" });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { result: { instructions: string } };
    expect(body.result.instructions).toBe(HTTP_MCP_INSTRUCTIONS);
  });

  it("lists mcp-tools schemas without write_handoff", async () => {
    const { fetchImpl } = mockFetch(() => ({ body: { unexpected: true } }));
    const app = appWith(fetchImpl);
    const res = await rpc(app, "tools/list");
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      result: { tools: Array<{ name: string; inputSchema: Record<string, unknown> }> };
    };
    const names = body.result.tools.map((tool) => tool.name);
    expect(names).toContain("get_project");
    expect(names).toContain("finish_work");
    expect(names).toContain("get_handoff");
    expect(names).not.toContain("write_handoff");
  });

  it("forwards tools/call through invoke and rejects write_handoff as unknown", async () => {
    const { fetchImpl, calls } = mockFetch((call) => {
      if (call.url.pathname === `/v1/projects/${PROJECT_ID}/decisions`) {
        return { body: { items: [], next_cursor: null } };
      }
      return { status: 500, body: { error: { code: "unauthorized", message: "unexpected" } } };
    });
    const app = appWith(fetchImpl);

    const listed = await rpc(app, "tools/call", { name: "list_decisions", arguments: {} });
    expect(listed.status).toBe(200);
    const listedBody = (await listed.json()) as { result: { content: Array<{ text: string }> } };
    expect(JSON.parse(listedBody.result.content[0]?.text ?? "{}")).toEqual({
      items: [],
      next_cursor: null,
    });
    expect(calls[0]?.headers.authorization).toBe(`Bearer ${TOKEN}`);
    expect(calls[0]?.url.pathname).toBe(`/v1/projects/${PROJECT_ID}/decisions`);

    const handoff = await rpc(app, "tools/call", {
      name: "write_handoff",
      arguments: { task_id: TASK_ID, summary: "Leaving a handoff without changing status" },
    });
    expect(handoff.status).toBe(200);
    const handoffBody = (await handoff.json()) as {
      result: { isError?: boolean; content: Array<{ text: string }> };
    };
    expect(handoffBody.result.isError).toBe(true);
    expect(JSON.parse(handoffBody.result.content[0]?.text ?? "{}")).toMatchObject({
      error: {
        status: 400,
        message: "unknown tool: write_handoff",
        details: { reason: "invalid_arguments" },
      },
    });
    expect(calls).toHaveLength(1);
  });

  it("maps missing index on code tools to 503 code_index_unavailable", async () => {
    const { fetchImpl } = mockFetch(() => ({
      status: 503,
      body: { error: { code: "code_index_unavailable", message: "no index" } },
    }));
    const app = appWith(fetchImpl);
    const res = await rpc(app, "tools/call", {
      name: "get_tree",
      arguments: { repo_id: PROJECT_ID },
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      result: { isError?: boolean; content: Array<{ text: string }> };
    };
    expect(body.result.isError).toBe(true);
    expect(JSON.parse(body.result.content[0]?.text ?? "{}")).toMatchObject({
      error: { code: "code_index_unavailable", status: 503 },
    });
  });
});
