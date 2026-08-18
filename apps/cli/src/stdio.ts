import { createInterface } from "node:readline";
import { Readable, Writable } from "node:stream";

import { invoke, isToolError, listToolDefinitions, type InvokeContext } from "@beacon/mcp-tools";

export const PROTOCOL_VERSION = "2024-11-05";
export const SERVER_NAME = "beacon";
export const SERVER_VERSION = "0.0.0";

const PARSE_ERROR = -32700;
const INVALID_REQUEST = -32600;
const METHOD_NOT_FOUND = -32601;
const INVALID_PARAMS = -32602;
const INTERNAL_ERROR = -32603;

type JsonRpcId = string | number | null;

type JsonRpcRequest = {
  jsonrpc: "2.0";
  id?: JsonRpcId;
  method?: unknown;
  params?: unknown;
};

type JsonRpcSuccess = {
  jsonrpc: "2.0";
  id: JsonRpcId;
  result: unknown;
};

type JsonRpcFailure = {
  jsonrpc: "2.0";
  id: JsonRpcId;
  error: { code: number; message: string; data?: unknown };
};

export type StdioServerOptions = {
  ctx: InvokeContext;
  input?: Readable;
  output?: Writable;
  requireProjectRead?: () => Promise<void>;
};

export class InitializeError extends Error {
  override readonly name = "InitializeError";
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

export async function assertProjectRead(ctx: InvokeContext): Promise<void> {
  if (!ctx.projectId) {
    throw new InitializeError(
      "unauthorized",
      "No project is configured. Set project_id in the Beacon config or BEACON_PROJECT.",
    );
  }
  const fetchImpl = ctx.fetch ?? fetch;
  const url = `${ctx.baseUrl.replace(/\/+$/, "")}/v1/projects/${ctx.projectId}`;
  let response: Response;
  try {
    response = await fetchImpl(url, {
      method: "GET",
      headers: {
        authorization: `Bearer ${ctx.token}`,
        accept: "application/json",
      },
    });
  } catch {
    throw new InitializeError("unavailable", "Could not reach Beacon.");
  }
  if (response.status === 401) {
    throw new InitializeError("unauthorized", "Token is invalid or expired.");
  }
  if (response.status === 403) {
    throw new InitializeError("forbidden", "This token needs project:read.");
  }
  if (response.status === 404) {
    throw new InitializeError("not_found", "Project not found for this token.");
  }
  if (response.status === 429) {
    throw new InitializeError(
      "rate_limited",
      "Beacon rate limited the request. Try again shortly.",
    );
  }
  if (!response.ok) {
    throw new InitializeError(
      "unavailable",
      `Could not verify project access (${response.status}).`,
    );
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

type ParsedLine = { request: JsonRpcRequest } | { error: JsonRpcFailure };

function parseMessage(line: string): ParsedLine {
  let parsed: unknown;
  try {
    parsed = JSON.parse(line) as unknown;
  } catch {
    return {
      error: {
        jsonrpc: "2.0",
        id: null,
        error: { code: PARSE_ERROR, message: "Parse error" },
      },
    };
  }
  if (!isObject(parsed) || parsed["jsonrpc"] !== "2.0") {
    return {
      error: {
        jsonrpc: "2.0",
        id: null,
        error: { code: INVALID_REQUEST, message: "Invalid Request" },
      },
    };
  }
  return { request: parsed as JsonRpcRequest };
}

function success(id: JsonRpcId, result: unknown): JsonRpcSuccess {
  return { jsonrpc: "2.0", id, result };
}

function failure(id: JsonRpcId, code: number, message: string, data?: unknown): JsonRpcFailure {
  return {
    jsonrpc: "2.0",
    id,
    error: data === undefined ? { code, message } : { code, message, data },
  };
}

function toolList() {
  return {
    tools: listToolDefinitions().map((tool) => ({
      name: tool.name,
      description: tool.description,
      inputSchema: tool.inputSchema,
    })),
  };
}

function initializeResult() {
  return {
    protocolVersion: PROTOCOL_VERSION,
    capabilities: { tools: {} },
    serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
  };
}

function textResult(text: string, isError = false) {
  return {
    content: [{ type: "text", text }],
    isError,
  };
}

export async function handleRpc(
  message: JsonRpcRequest,
  ctx: InvokeContext,
  state: { initialized: boolean },
  requireProjectRead: () => Promise<void>,
): Promise<JsonRpcSuccess | JsonRpcFailure | undefined> {
  const method = typeof message.method === "string" ? message.method : undefined;
  const hasId = Object.prototype.hasOwnProperty.call(message, "id");
  const id = hasId ? (message.id as JsonRpcId) : null;

  if (!method) {
    if (!hasId) {
      return undefined;
    }
    return failure(id, INVALID_REQUEST, "Invalid Request");
  }

  if (method.startsWith("notifications/")) {
    return undefined;
  }

  if (!hasId) {
    return undefined;
  }

  if (method === "initialize") {
    try {
      await requireProjectRead();
    } catch (error) {
      if (error instanceof InitializeError) {
        return failure(id, INVALID_PARAMS, error.message, { code: error.code });
      }
      throw error;
    }
    state.initialized = true;
    return success(id, initializeResult());
  }

  if (method === "ping") {
    return success(id, {});
  }

  if (method === "tools/list") {
    if (!state.initialized) {
      return failure(id, INVALID_REQUEST, "initialize is required");
    }
    return success(id, toolList());
  }

  if (method === "tools/call") {
    if (!state.initialized) {
      return failure(id, INVALID_REQUEST, "initialize is required");
    }
    const params = isObject(message.params) ? message.params : undefined;
    const name = typeof params?.["name"] === "string" ? params["name"] : undefined;
    if (!name) {
      return failure(id, INVALID_PARAMS, "tool name is required");
    }
    const args = params?.["arguments"] ?? {};
    try {
      const result = await invoke(name, args, ctx);
      return success(id, textResult(JSON.stringify(result)));
    } catch (error) {
      if (isToolError(error)) {
        return success(
          id,
          textResult(
            JSON.stringify({
              error: {
                code: error.code,
                message: error.message,
                details: error.details,
                status: error.status,
              },
            }),
            true,
          ),
        );
      }
      const messageText = error instanceof Error ? error.message : "internal error";
      return failure(id, INTERNAL_ERROR, messageText);
    }
  }

  return failure(id, METHOD_NOT_FOUND, `Method not found: ${method}`);
}

export async function serveStdio(options: StdioServerOptions): Promise<void> {
  const input = options.input ?? process.stdin;
  const output = options.output ?? process.stdout;
  const requireProjectRead = options.requireProjectRead ?? (() => assertProjectRead(options.ctx));
  const state = { initialized: false };
  const rl = createInterface({ input, crlfDelay: Infinity });

  const write = (payload: JsonRpcSuccess | JsonRpcFailure) => {
    output.write(`${JSON.stringify(payload)}\n`);
  };

  for await (const line of rl) {
    if (!line.trim()) {
      continue;
    }
    const parsed = parseMessage(line);
    if ("error" in parsed) {
      write(parsed.error);
      continue;
    }
    const reply = await handleRpc(parsed.request, options.ctx, state, requireProjectRead);
    if (reply) {
      write(reply);
    }
  }
}
