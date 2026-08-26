import { describe, expect, it } from "vitest";

import {
  generateProjectToken,
  hashProjectToken,
  projectTokenDisplayPrefix,
} from "../auth/project-tokens.js";
import { DEFAULT_RATE_LIMITS } from "../auth/rate-limit.js";
import { MemoryAuthStore } from "../auth/store.js";
import { attachSidecarTunnelSocket, WS_OPEN } from "./ws.js";
import { createSidecarTunnelHub } from "./tunnel.js";

const NOW = new Date("2026-01-01T00:00:00.000Z");
const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ab";
const REPO_ID = "01934567-89ab-7cde-89ab-0123456789aa";

function testConfig() {
  return {
    bootstrapAdminToken: undefined,
    workerToken: undefined,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    secureCookies: false,
    trustProxy: false,
    indexRpcUrl: "http://127.0.0.1:7744",
    indexRpcToken: "index-rpc-test",
  };
}

function fakeSocket() {
  const listeners = new Map<string, Array<(data?: unknown) => void>>();
  const sent: string[] = [];
  let closed: { code?: number; reason?: string } | undefined;
  return {
    readyState: WS_OPEN,
    sent,
    get closed() {
      return closed;
    },
    send(data: string | ArrayBuffer | Uint8Array) {
      sent.push(typeof data === "string" ? data : Buffer.from(data as Buffer).toString("utf8"));
    },
    close(code?: number, reason?: string) {
      closed = { code, reason };
    },
    on(event: string, listener: (...args: unknown[]) => void) {
      const list = listeners.get(event) ?? [];
      list.push(listener);
      listeners.set(event, list);
    },
    emit(event: string, data?: unknown) {
      for (const listener of listeners.get(event) ?? []) {
        listener(data);
      }
    },
  };
}

async function seedProject(store: MemoryAuthStore, scopes: string[]) {
  const secret = generateProjectToken();
  await store.createApiToken({
    id: "tok-1",
    projectId: PROJECT_ID,
    name: "sidecar",
    tokenHash: hashProjectToken(secret),
    prefix: projectTokenDisplayPrefix(secret),
    scopes: scopes as never,
    createdBy: null,
    lastUsedAt: null,
    expiresAt: null,
    revokedAt: null,
    createdAt: NOW,
  });
  await store.createProjectRepo({
    id: REPO_ID,
    projectId: PROJECT_ID,
    provider: "local",
    remoteUrl: null,
    defaultBranch: "main",
    githubRepoId: null,
    installationId: null,
    localRootHint: ".",
    indexMode: "sidecar",
    lastIndexedSha: null,
    lastIndexedAt: null,
  });
  return secret;
}

function attach(store: MemoryAuthStore, socket: ReturnType<typeof fakeSocket>) {
  attachSidecarTunnelSocket({
    ws: socket,
    request: new Request("http://127.0.0.1:8080/v1/sidecar"),
    deps: {
      store,
      config: testConfig(),
      clock: { now: () => NOW },
      githubFetch: fetch,
      rateLimits: DEFAULT_RATE_LIMITS,
      setOrgId: (() => {}) as (orgId: string | null) => void,
    },
    hub: createSidecarTunnelHub({ log: () => undefined }),
    env: { FF_SIDECAR_TUNNEL: "true" },
  });
}

async function flush(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe("sidecar tunnel handshake", () => {
  it("rejects an invalid token without remapping to 404", async () => {
    const store = new MemoryAuthStore();
    const socket = fakeSocket();
    attach(store, socket);
    socket.emit("message", JSON.stringify({ type: "hello", token: generateProjectToken() }));
    await flush();
    expect(JSON.parse(socket.sent[0] ?? "{}")).toMatchObject({
      type: "error",
      code: "unauthorized",
      message: "invalid token",
    });
    expect(socket.closed).toMatchObject({ code: 1008, reason: "invalid token" });
  });

  it("rejects a token without code:read as insufficient scope", async () => {
    const store = new MemoryAuthStore();
    const secret = await seedProject(store, ["project:read"]);
    const socket = fakeSocket();
    attach(store, socket);
    socket.emit("message", JSON.stringify({ type: "hello", token: secret }));
    await flush();
    expect(JSON.parse(socket.sent[0] ?? "{}")).toMatchObject({
      type: "error",
      code: "forbidden",
      message: "insufficient token scope",
    });
    expect(socket.closed).toMatchObject({ code: 1008, reason: "insufficient token scope" });
    expect(socket.sent[0]).not.toContain("not_found");
  });

  it("rejects unknown repo_ids as repo not found", async () => {
    const store = new MemoryAuthStore();
    const secret = await seedProject(store, ["project:read", "code:read"]);
    const socket = fakeSocket();
    attach(store, socket);
    socket.emit(
      "message",
      JSON.stringify({
        type: "hello",
        token: secret,
        repo_ids: ["01934567-89ab-7cde-89ab-0123456789ff"],
      }),
    );
    await flush();
    expect(JSON.parse(socket.sent[0] ?? "{}")).toMatchObject({
      type: "error",
      code: "not_found",
      message: "repo not found",
    });
    expect(socket.closed).toMatchObject({ code: 1008, reason: "repo not found" });
  });
});
