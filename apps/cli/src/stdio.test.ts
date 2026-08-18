import { PassThrough } from "node:stream";
import { describe, expect, it } from "vitest";

import { TOOL_NAMES, type InvokeContext } from "@beacon/mcp-tools";

import { unavailableCodeSource } from "./code-source.js";
import { assertProjectRead, handleRpc, PROTOCOL_VERSION, serveStdio } from "./stdio.js";

const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";
const TASK_ID = "01934567-89ab-7cde-89ab-0123456789ab";

type Recorded = { method: string; url: string; headers: Record<string, string> };

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

function mockFetch(handler: (call: Recorded) => { status?: number; body?: unknown }) {
  const calls: Recorded[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const call = {
      method: (init?.method ?? "GET").toUpperCase(),
      url,
      headers: headerMap(init?.headers),
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

function ctx(fetchImpl: typeof fetch, extra: Partial<InvokeContext> = {}): InvokeContext {
  return {
    baseUrl: "https://beacon.test",
    token: "bcn_" + "A".repeat(43),
    projectId: PROJECT_ID,
    fetch: fetchImpl,
    codeSource: unavailableCodeSource(),
    ...extra,
  };
}

describe("stdio MCP", () => {
  it("fails initialize when Beacon is unreachable", async () => {
    const fetchImpl = (async () => {
      throw new Error("ECONNREFUSED");
    }) as typeof fetch;
    const reply = await handleRpc(
      { jsonrpc: "2.0", id: 1, method: "initialize", params: {} },
      ctx(fetchImpl),
      { initialized: false },
      () => assertProjectRead(ctx(fetchImpl)),
    );
    expect(reply).toMatchObject({
      error: { message: "Could not reach Beacon.", data: { code: "unavailable" } },
    });
  });

  it("fails initialize without project:read", async () => {
    const { fetchImpl } = mockFetch(() => ({
      status: 403,
      body: { error: { code: "forbidden", message: "insufficient token scope" } },
    }));
    const reply = await handleRpc(
      {
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
          protocolVersion: PROTOCOL_VERSION,
          capabilities: {},
          clientInfo: { name: "test", version: "0" },
        },
      },
      ctx(fetchImpl),
      { initialized: false },
      () => assertProjectRead(ctx(fetchImpl)),
    );
    expect(reply).toMatchObject({
      error: { message: "This token needs project:read.", data: { code: "forbidden" } },
    });
  });

  it("lists the catalog after a successful initialize", async () => {
    const { fetchImpl } = mockFetch(() => ({ body: { items: [], next_cursor: null } }));
    const state = { initialized: false };
    const init = await handleRpc(
      { jsonrpc: "2.0", id: 1, method: "initialize", params: {} },
      ctx(fetchImpl),
      state,
      () => assertProjectRead(ctx(fetchImpl)),
    );
    expect(init).toMatchObject({
      result: { protocolVersion: PROTOCOL_VERSION, capabilities: { tools: {} } },
    });
    expect(state.initialized).toBe(true);

    const listed = await handleRpc(
      { jsonrpc: "2.0", id: 2, method: "tools/list" },
      ctx(fetchImpl),
      state,
      async () => undefined,
    );
    const tools = (listed as { result: { tools: { name: string; description?: string }[] } }).result
      .tools;
    expect(tools.map((tool) => tool.name).sort()).toEqual([...TOOL_NAMES].sort());
    expect(tools.every((tool) => (tool.description ?? "").length > 8)).toBe(true);
  });

  it("forwards non-code tools to /v1 and returns 503 for code tools", async () => {
    const { fetchImpl, calls } = mockFetch((call) => {
      if (call.url.endsWith(`/v1/projects/${PROJECT_ID}`)) {
        return { body: { id: PROJECT_ID, name: "Beacon" } };
      }
      if (call.url.includes("/sessions")) {
        return { body: { items: [], next_cursor: null } };
      }
      return { body: { id: TASK_ID } };
    });
    const state = { initialized: true };
    const project = await handleRpc(
      {
        jsonrpc: "2.0",
        id: 3,
        method: "tools/call",
        params: { name: "get_project", arguments: {} },
      },
      ctx(fetchImpl),
      state,
      async () => undefined,
    );
    expect(calls.some((call) => call.url.endsWith(`/v1/projects/${PROJECT_ID}`))).toBe(true);
    expect(calls[0]?.headers["authorization"]).toMatch(/^Bearer bcn_/);
    const text = (project as { result: { content: { text: string }[] } }).result.content[0]?.text;
    expect(text).toContain(PROJECT_ID);

    const tree = await handleRpc(
      { jsonrpc: "2.0", id: 4, method: "tools/call", params: { name: "get_tree", arguments: {} } },
      ctx(fetchImpl),
      state,
      async () => undefined,
    );
    const treeText = (tree as { result: { content: { text: string }[]; isError: boolean } }).result;
    expect(treeText.isError).toBe(true);
    expect(treeText.content[0]?.text).toContain("code_index_unavailable");
  });

  it("does not remap write_handoff onto finish", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    const reply = await handleRpc(
      {
        jsonrpc: "2.0",
        id: 5,
        method: "tools/call",
        params: {
          name: "write_handoff",
          arguments: { session_id: TASK_ID, summary: "Leaving notes without changing status" },
        },
      },
      ctx(fetchImpl),
      { initialized: true },
      async () => undefined,
    );
    expect(calls).toHaveLength(0);
    const body = JSON.parse(
      (reply as { result: { content: { text: string }[] } }).result.content[0]!.text,
    ) as {
      error: { status: number };
    };
    expect(body.error.status).toBe(501);
  });

  it("serves newline-delimited JSON-RPC on stdio", async () => {
    const input = new PassThrough();
    const output = new PassThrough();
    const chunks: string[] = [];
    output.on("data", (chunk: Buffer | string) => {
      chunks.push(String(chunk));
    });
    const { fetchImpl } = mockFetch(() => ({ body: { items: [] } }));
    const serving = serveStdio({
      ctx: ctx(fetchImpl),
      input,
      output,
      requireProjectRead: async () => undefined,
    });
    input.write(`${JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: {} })}\n`);
    input.end();
    await serving;
    const parsed = JSON.parse(chunks.join("").trim()) as {
      result: { serverInfo: { name: string } };
    };
    expect(parsed.result.serverInfo.name).toBe("beacon");
  });
});
