const BEARER_PREFIX = "Bearer ";

export type JsonRpcId = string | number | null;

export type JsonRpcErrorBody = {
  jsonrpc: "2.0";
  error: { code: number; message: string; data?: Record<string, unknown> };
  id: JsonRpcId;
};

export function parseBearer(header: string | undefined): string | undefined {
  if (!header) {
    return undefined;
  }
  if (!header.startsWith(BEARER_PREFIX)) {
    return undefined;
  }
  const token = header.slice(BEARER_PREFIX.length).trim();
  return token.length > 0 ? token : undefined;
}

export function jsonRpcError(
  id: JsonRpcId,
  code: number,
  message: string,
  data?: Record<string, unknown>,
): JsonRpcErrorBody {
  return {
    jsonrpc: "2.0",
    error: data ? { code, message, data } : { code, message },
    id,
  };
}

export function unauthorizedRpc(id: JsonRpcId = null, message = "unauthorized"): JsonRpcErrorBody {
  return jsonRpcError(id, -32001, message, { code: "unauthorized" });
}

export function forbiddenRpc(
  id: JsonRpcId = null,
  message = "initialize requires project:read",
): JsonRpcErrorBody {
  return jsonRpcError(id, -32003, message, { code: "forbidden" });
}

export function invalidRequestRpc(id: JsonRpcId = null, message: string): JsonRpcErrorBody {
  return jsonRpcError(id, -32602, message, { code: "invalid_request" });
}

export function internalRpc(id: JsonRpcId = null, message = "internal error"): JsonRpcErrorBody {
  return jsonRpcError(id, -32603, message, { code: "internal_error" });
}

export function extractJsonRpcId(body: unknown): JsonRpcId {
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return null;
  }
  const id = (body as { id?: unknown }).id;
  if (typeof id === "string" || typeof id === "number") {
    return id;
  }
  return null;
}

export function isJsonRpcNotification(body: unknown): boolean {
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return false;
  }
  const record = body as { jsonrpc?: unknown; method?: unknown; id?: unknown };
  return record.jsonrpc === "2.0" && typeof record.method === "string" && record.id === undefined;
}
