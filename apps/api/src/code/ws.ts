import { actorHasCapability } from "../auth/access.js";
import { hashProjectToken, isProjectTokenFormat } from "../auth/project-tokens.js";
import type { AuthDeps } from "../auth/routes.js";
import type { RepoStore } from "../repos/store.js";
import type { TokenStore } from "../tokens/store.js";
import { parseBearer } from "../auth/tokens.js";
import { isSidecarTunnelEnabled } from "./flags.js";
import {
  asTunnelHello,
  asTunnelResponse,
  parseTunnelJson,
  TUNNEL_RPC_TIMEOUT_MS,
  type SidecarTunnelHub,
  type TunnelRequest,
} from "./tunnel.js";

export type WsLike = {
  readyState?: number;
  send(data: string | ArrayBuffer | Uint8Array): void;
  close(code?: number, reason?: string): void;
  on?(event: string, listener: (...args: unknown[]) => void): void;
};

export const WS_OPEN = 1;

function onSocket(
  ws: WsLike,
  type: "message" | "close" | "error",
  listener: (data?: unknown) => void,
): void {
  ws.on?.(type, listener);
}

function socketText(data: unknown): string | undefined {
  if (typeof data === "string") {
    return data;
  }
  if (data instanceof ArrayBuffer) {
    return Buffer.from(data).toString("utf8");
  }
  if (ArrayBuffer.isView(data)) {
    return Buffer.from(data.buffer, data.byteOffset, data.byteLength).toString("utf8");
  }
  return undefined;
}

function sendJson(ws: WsLike, body: unknown): void {
  if (ws.readyState !== undefined && ws.readyState !== WS_OPEN) {
    return;
  }
  ws.send(JSON.stringify(body));
}

export function attachSidecarTunnelSocket(options: {
  ws: WsLike;
  request: Request;
  deps: AuthDeps & { store: TokenStore & RepoStore };
  hub: SidecarTunnelHub;
  env?: NodeJS.ProcessEnv;
  timeoutMs?: number;
}): void {
  const env = options.env ?? process.env;
  const timeoutMs = options.timeoutMs ?? TUNNEL_RPC_TIMEOUT_MS;
  let sessionId: string | undefined;
  let closed = false;
  const pending = new Map<
    string,
    { resolve: (value: { status: number; body: unknown }) => void; reject: (error: Error) => void }
  >();

  const finish = () => {
    if (closed) {
      return;
    }
    closed = true;
    if (sessionId) {
      options.hub.unregister(sessionId);
    }
    for (const waiter of pending.values()) {
      waiter.reject(new Error("sidecar tunnel closed"));
    }
    pending.clear();
  };

  const fail = (code: number, message: { type: "error"; code: string; message: string }) => {
    sendJson(options.ws, message);
    options.ws.close(code, message.message);
    finish();
  };

  onSocket(options.ws, "close", finish);
  onSocket(options.ws, "error", finish);
  onSocket(options.ws, "message", (event) => {
    void (async () => {
      if (closed) {
        return;
      }
      const text = socketText(
        event && typeof event === "object" && "data" in event
          ? (event as { data?: unknown }).data
          : event,
      );
      if (text === undefined) {
        return;
      }
      const parsed = parseTunnelJson(text);
      if (!sessionId) {
        if (!isSidecarTunnelEnabled(env)) {
          fail(1008, { type: "error", code: "not_found", message: "not found" });
          return;
        }
        const hello = asTunnelHello(parsed);
        if (!hello) {
          fail(1008, { type: "error", code: "unauthorized", message: "invalid token" });
          return;
        }
        const token =
          hello.token ?? parseBearer(options.request.headers.get("authorization") ?? undefined);
        if (!token || !isProjectTokenFormat(token)) {
          fail(1008, { type: "error", code: "unauthorized", message: "invalid token" });
          return;
        }
        const record = await options.deps.store.findApiTokenByHash(hashProjectToken(token));
        const now = options.deps.clock.now();
        if (
          !record ||
          record.revokedAt ||
          (record.expiresAt !== null && record.expiresAt.getTime() <= now.getTime())
        ) {
          fail(1008, { type: "error", code: "unauthorized", message: "invalid token" });
          return;
        }
        if (!actorHasCapability({ kind: "token", token: record }, "code:read")) {
          fail(1008, { type: "error", code: "forbidden", message: "insufficient token scope" });
          return;
        }
        await options.deps.store.touchApiToken(record.id, now);
        const projectRepos = await options.deps.store.listProjectRepos(record.projectId);
        const allowed = new Set(projectRepos.map((repo) => repo.id));
        const requested =
          hello.repo_ids && hello.repo_ids.length > 0 ? hello.repo_ids : [...allowed];
        const repoIds = requested.filter((id) => allowed.has(id));
        if (repoIds.length === 0) {
          fail(1008, { type: "error", code: "not_found", message: "repo not found" });
          return;
        }
        for (const repoId of repoIds) {
          await options.deps.store.upsertSidecarConnection({
            id: record.id,
            repoId,
            tokenId: record.id,
            now,
          });
        }
        sessionId = options.hub.register({
          projectId: record.projectId,
          tokenId: record.id,
          repoIds: new Set(repoIds),
          request(repoId, path, query) {
            const id = randomRequestId();
            const payload: TunnelRequest = { type: "req", id, repo_id: repoId, path, query };
            return new Promise((resolve, reject) => {
              const timer = setTimeout(() => {
                pending.delete(id);
                reject(new Error("sidecar tunnel timeout"));
              }, timeoutMs);
              pending.set(id, {
                resolve: (value) => {
                  clearTimeout(timer);
                  resolve(value);
                },
                reject: (error) => {
                  clearTimeout(timer);
                  reject(error);
                },
              });
              try {
                sendJson(options.ws, payload);
              } catch (error) {
                pending.delete(id);
                clearTimeout(timer);
                reject(error instanceof Error ? error : new Error("sidecar tunnel send failed"));
              }
            });
          },
        });
        sendJson(options.ws, { type: "ok", repo_ids: repoIds });
        return;
      }
      const response = asTunnelResponse(parsed);
      if (!response) {
        return;
      }
      const waiter = pending.get(response.id);
      if (!waiter) {
        return;
      }
      pending.delete(response.id);
      waiter.resolve({ status: response.status, body: response.body });
    })();
  });
}

function randomRequestId(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
