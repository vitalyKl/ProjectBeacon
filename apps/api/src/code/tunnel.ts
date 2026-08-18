import { randomUUID } from "node:crypto";

import { CodeGatewayError } from "./gateway.js";

export const SIDECAR_TUNNEL_PATH = "/v1/sidecar";
export const TUNNEL_RPC_TIMEOUT_MS = 10_000;

export type TunnelRpcQuery = Record<
  string,
  string | number | Array<string | undefined> | undefined
>;

export type TunnelHello = {
  type: "hello";
  token?: string;
  repo_ids?: string[];
};

export type TunnelRequest = {
  type: "req";
  id: string;
  repo_id: string;
  path: string;
  query?: TunnelRpcQuery;
};

export type TunnelResponse = {
  type: "res";
  id: string;
  status: number;
  body?: unknown;
};

export type TunnelAck = {
  type: "ok";
  repo_ids: string[];
};

export type TunnelErrorMessage = {
  type: "error";
  code: string;
  message: string;
};

export type SidecarTunnelSession = {
  id: string;
  projectId: string;
  tokenId: string;
  repoIds: ReadonlySet<string>;
  request(
    repoId: string,
    path: string,
    query?: TunnelRpcQuery,
  ): Promise<{ status: number; body: unknown }>;
};

export type SidecarTunnelHub = {
  register(session: Omit<SidecarTunnelSession, "id"> & { id?: string }): string;
  unregister(id: string): void;
  isLive(repoId: string): boolean;
  query(repoId: string, path: string, query?: TunnelRpcQuery): Promise<unknown>;
};

function logTunnel(fields: Record<string, unknown>): void {
  console.log(JSON.stringify({ level: "info", msg: "sidecar_tunnel", ...fields }));
}

export function parseTunnelJson(raw: string): unknown {
  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return undefined;
  }
}

export function asTunnelHello(value: unknown): TunnelHello | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const record = value as { type?: unknown; token?: unknown; repo_ids?: unknown };
  if (record.type !== "hello") {
    return undefined;
  }
  const repoIds = Array.isArray(record.repo_ids)
    ? record.repo_ids.filter((item): item is string => typeof item === "string" && item.length > 0)
    : undefined;
  return {
    type: "hello",
    token: typeof record.token === "string" && record.token.length > 0 ? record.token : undefined,
    repo_ids: repoIds,
  };
}

export function asTunnelResponse(value: unknown): TunnelResponse | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const record = value as { type?: unknown; id?: unknown; status?: unknown; body?: unknown };
  if (record.type !== "res" || typeof record.id !== "string" || typeof record.status !== "number") {
    return undefined;
  }
  return { type: "res", id: record.id, status: record.status, body: record.body };
}

export function createSidecarTunnelHub(
  options: {
    now?: () => Date;
    log?: (fields: Record<string, unknown>) => void;
  } = {},
): SidecarTunnelHub {
  const sessions = new Map<string, SidecarTunnelSession>();
  const byRepo = new Map<string, string>();
  const log = options.log ?? logTunnel;

  return {
    register(session) {
      const id = session.id ?? randomUUID();
      const next: SidecarTunnelSession = { ...session, id };
      sessions.set(id, next);
      for (const repoId of next.repoIds) {
        byRepo.set(repoId, id);
      }
      log({ event: "open", session_id: id, repo_count: next.repoIds.size });
      return id;
    },
    unregister(id) {
      const session = sessions.get(id);
      if (!session) {
        return;
      }
      sessions.delete(id);
      for (const repoId of session.repoIds) {
        if (byRepo.get(repoId) === id) {
          byRepo.delete(repoId);
        }
      }
      log({ event: "close", session_id: id });
    },
    isLive(repoId) {
      const sessionId = byRepo.get(repoId);
      return Boolean(sessionId && sessions.has(sessionId));
    },
    async query(repoId, path, query) {
      const sessionId = byRepo.get(repoId);
      const session = sessionId ? sessions.get(sessionId) : undefined;
      if (!session) {
        throw new CodeGatewayError({
          status: 503,
          code: "code_index_unavailable",
          message: "code index unavailable",
        });
      }
      const started = options.now?.().getTime() ?? Date.now();
      let status = 503;
      try {
        const result = await session.request(repoId, path, query);
        status = result.status;
        if (result.status < 200 || result.status >= 300) {
          const err =
            result.body !== null && typeof result.body === "object" && !Array.isArray(result.body)
              ? (
                  result.body as {
                    error?: { code?: string; message?: string; details?: Record<string, unknown> };
                  }
                ).error
              : undefined;
          throw new CodeGatewayError({
            status: result.status,
            code: err?.code ?? (result.status === 503 ? "code_index_unavailable" : "unauthorized"),
            message: err?.message ?? "index request failed",
            details: err?.details ?? {},
          });
        }
        return result.body;
      } finally {
        log({
          event: "request",
          repo_id: repoId,
          path,
          status,
          ms: (options.now?.().getTime() ?? Date.now()) - started,
        });
      }
    },
  };
}
