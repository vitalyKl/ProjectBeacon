import { invoke, isToolError, listToolDefinitions, type InvokeContext } from "@beacon/mcp-tools";

import {
  extractJsonRpcId,
  forbiddenRpc,
  internalRpc,
  jsonRpcError,
  unauthorizedRpc,
  type JsonRpcId,
} from "./auth.js";
import { assertProjectRead, type SessionContext } from "./session.js";

export const HTTP_MCP_INSTRUCTIONS =
  "Control plane (context & tasks). Code tools need a local sidecar, a self-host bind-mount, or an enabled hosted clone.";

const SUPPORTED_PROTOCOL_VERSIONS = new Set(["2025-06-18", "2025-03-26", "2024-11-05"]);
const DEFAULT_PROTOCOL_VERSION = "2025-03-26";

type JsonRpcRequest = {
  jsonrpc?: unknown;
  id?: unknown;
  method?: unknown;
  params?: unknown;
};

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  return value as Record<string, unknown>;
}

function protocolVersionOf(params: unknown): string {
  const record = asRecord(params);
  const version = record?.["protocolVersion"];
  if (typeof version === "string" && SUPPORTED_PROTOCOL_VERSIONS.has(version)) {
    return version;
  }
  return DEFAULT_PROTOCOL_VERSION;
}

function toolCallParams(params: unknown): { name: string; args: unknown } | undefined {
  const record = asRecord(params);
  if (!record || typeof record["name"] !== "string") {
    return undefined;
  }
  return { name: record["name"], args: record["arguments"] ?? {} };
}

function result(id: JsonRpcId, value: unknown) {
  return { jsonrpc: "2.0" as const, id, result: value };
}

async function handleInitialize(id: JsonRpcId, params: unknown, session: SessionContext) {
  try {
    await assertProjectRead(session);
  } catch (error) {
    if (isToolError(error)) {
      if (error.code === "unauthorized") {
        return unauthorizedRpc(id, error.message);
      }
      if (error.code === "forbidden") {
        return forbiddenRpc(id);
      }
      if (error.code === "not_found") {
        return forbiddenRpc(id, "project not available");
      }
      return internalRpc(id);
    }
    return internalRpc(id);
  }

  return result(id, {
    protocolVersion: protocolVersionOf(params),
    capabilities: { tools: {} },
    serverInfo: { name: "beacon", version: "0.0.0" },
    instructions: HTTP_MCP_INSTRUCTIONS,
  });
}

function handleToolsList(id: JsonRpcId) {
  return result(id, {
    tools: listToolDefinitions().map((tool) => ({
      name: tool.name,
      inputSchema: tool.inputSchema,
    })),
  });
}

async function handleToolsCall(id: JsonRpcId, params: unknown, session: SessionContext) {
  const call = toolCallParams(params);
  if (!call) {
    return jsonRpcError(id, -32602, "invalid tool arguments");
  }

  const ctx: InvokeContext = {
    baseUrl: session.baseUrl,
    token: session.token,
    projectId: session.projectId,
    fetch: session.fetch,
  };

  try {
    const value = await invoke(call.name, call.args, ctx);
    return result(id, {
      content: [{ type: "text", text: JSON.stringify(value) }],
    });
  } catch (error) {
    if (!isToolError(error)) {
      return jsonRpcError(id, -32603, "internal error");
    }
    return result(id, {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: {
              code: error.code,
              message: error.message,
              details: error.details,
              status: error.status,
            },
          }),
        },
      ],
      isError: true,
    });
  }
}

export async function handleJsonRpc(
  body: unknown,
  session: SessionContext,
): Promise<{ status: number; body: unknown } | { status: number }> {
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return { status: 200, body: jsonRpcError(null, -32700, "parse error") };
  }

  const request = body as JsonRpcRequest;
  if (request.jsonrpc !== "2.0" || typeof request.method !== "string") {
    return { status: 200, body: jsonRpcError(extractJsonRpcId(body), -32600, "invalid request") };
  }

  const id = extractJsonRpcId(body);
  const isNotification = request.id === undefined;

  switch (request.method) {
    case "initialize":
      if (isNotification) {
        return { status: 202 };
      }
      return { status: 200, body: await handleInitialize(id, request.params, session) };
    case "notifications/initialized":
    case "initialized":
      return { status: 202 };
    case "ping":
      if (isNotification) {
        return { status: 202 };
      }
      return { status: 200, body: result(id, {}) };
    case "tools/list":
      if (isNotification) {
        return { status: 202 };
      }
      return { status: 200, body: handleToolsList(id) };
    case "tools/call":
      if (isNotification) {
        return { status: 202 };
      }
      return { status: 200, body: await handleToolsCall(id, request.params, session) };
    default:
      if (isNotification) {
        return { status: 202 };
      }
      return { status: 200, body: jsonRpcError(id, -32601, `method not found: ${request.method}`) };
  }
}
