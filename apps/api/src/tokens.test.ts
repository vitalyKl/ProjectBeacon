import { DEFAULT_TOKEN_SCOPES, uuidv7 } from "@beacon/shared";
import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { PROJECT_TOKEN_LENGTH, PROJECT_TOKEN_PREFIX } from "./auth/project-tokens.js";
import { DEFAULT_RATE_LIMITS } from "./auth/rate-limit.js";
import { MemoryAuthStore } from "./auth/store.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    workerToken: undefined,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    secureCookies: false,
    trustProxy: false,
    workerToken: undefined,
    ...overrides,
  };
}

function sessionCookie(res: Response): string | undefined {
  const header = res.headers.get("set-cookie");
  if (!header) {
    return undefined;
  }
  const match = /(?:^|,\s*)beacon_session=([^;]+)/.exec(header);
  return match?.[1];
}

function cookieHeader(token: string): string {
  return `beacon_session=${token}`;
}

async function bootstrapWithProject(
  store = new MemoryAuthStore(),
  options: { clock?: { now: () => Date } } = {},
) {
  const app = createApp({
    store,
    config: testConfig(),
    checkReady: async () => true,
    enableTokenProbe: true,
    clock: options.clock,
  });
  const boot = await app.request("/v1/auth/bootstrap", {
    method: "POST",
    headers: {
      authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
  });
  const cookie = sessionCookie(boot)!;
  const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(cookie) } });
  const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
  const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
    method: "POST",
    headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
    body: JSON.stringify({ slug: "beacon", name: "Beacon" }),
  });
  const project = (await created.json()) as { id: string };
  return { app, store, cookie, projectId: project.id };
}

describe("project tokens", () => {
  it("mints a secret once and never lists it", async () => {
    const { app, cookie, projectId } = await bootstrapWithProject();

    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "cli" }),
    });
    expect(minted.status).toBe(201);
    const created = (await minted.json()) as {
      id: string;
      token: string;
      prefix: string;
      scopes: string[];
      expires_at: string | null;
    };
    expect(created.token.startsWith(PROJECT_TOKEN_PREFIX)).toBe(true);
    expect(created.token).toHaveLength(PROJECT_TOKEN_LENGTH);
    expect(created.prefix).toBe(created.token.slice(0, 8));
    expect(created.scopes).toEqual([...DEFAULT_TOKEN_SCOPES]);
    expect(created.scopes).not.toContain("code:read");
    expect(created.expires_at).not.toBeNull();

    const listed = await app.request(`/v1/projects/${projectId}/tokens`, {
      headers: { cookie: cookieHeader(cookie) },
    });
    expect(listed.status).toBe(200);
    const body = (await listed.json()) as { items: Record<string, unknown>[] };
    expect(body.items).toHaveLength(1);
    expect(body.items[0]).not.toHaveProperty("token");
    expect(body.items[0]).toMatchObject({ id: created.id, prefix: created.prefix });
  });

  it("rejects an invalid bearer and accepts a minted token", async () => {
    const { app, cookie, projectId } = await bootstrapWithProject();
    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "agent" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const invalid = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: "Bearer not-a-real-token", "content-type": "application/json" },
      body: JSON.stringify({ status: "ready" }),
    });
    expect(invalid.status).toBe(401);

    const probe = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ status: "ready" }),
    });
    expect(probe.status).toBe(200);
    expect(await probe.json()).toMatchObject({ actor: "token", status: "backlog" });
  });

  it("returns 401 after revoke", async () => {
    const { app, cookie, projectId } = await bootstrapWithProject();
    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "temp" }),
    });
    const created = (await minted.json()) as { id: string; token: string };

    const revoked = await app.request(`/v1/tokens/${created.id}/revoke`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie) },
    });
    expect(revoked.status).toBe(200);
    expect(await revoked.json()).toMatchObject({ id: created.id, revoked_at: expect.any(String) });

    const after = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${created.token}`, "content-type": "application/json" },
      body: "{}",
    });
    expect(after.status).toBe(401);
  });

  it("does not let a non-admin token mint", async () => {
    const { app, cookie, projectId } = await bootstrapWithProject();
    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "limited" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const denied = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ name: "second" }),
    });
    expect(denied.status).toBe(403);
  });

  it("rate limits tokens with injected tiny caps", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      enableTokenProbe: true,
      rateLimits: { ...DEFAULT_RATE_LIMITS, tokenPerMin: 1, burstMultiplier: 1 },
    });
    const boot = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const cookie = sessionCookie(boot)!;
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(cookie) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ slug: "rate", name: "Rate" }),
    });
    const projectId = ((await created.json()) as { id: string }).id;
    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "rated" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const first = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: "{}",
    });
    expect(first.status).toBe(200);

    const second = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: "{}",
    });
    expect(second.status).toBe(429);
    expect(second.headers.get("retry-after")).toMatch(/^\d+$/);
    expect(await second.json()).toMatchObject({ error: { code: "rate_limited" } });
  });

  it("lists and resolves pending approvals", async () => {
    const { app, cookie, projectId } = await bootstrapWithProject();
    const created = await app.request(`/v1/projects/${projectId}/approvals`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ action: "constraints.apply", payload: { id: "x" } }),
    });
    expect(created.status).toBe(201);
    const approval = (await created.json()) as { id: string; status: string };

    const listed = await app.request(`/v1/projects/${projectId}/approvals`, {
      headers: { cookie: cookieHeader(cookie) },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toMatchObject({
      items: [expect.objectContaining({ id: approval.id, status: "pending" })],
    });

    const resolved = await app.request(`/v1/approvals/${approval.id}/resolve`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ decision: "approved" }),
    });
    expect(resolved.status).toBe(200);
    expect(await resolved.json()).toMatchObject({ id: approval.id, status: "approved" });

    const after = await app.request(`/v1/projects/${projectId}/approvals`, {
      headers: { cookie: cookieHeader(cookie) },
    });
    expect(((await after.json()) as { items: unknown[] }).items).toHaveLength(0);
  });

  it("rejects unknown or cross-project session_id on approval create", async () => {
    const store = new MemoryAuthStore();
    const { app, cookie, projectId } = await bootstrapWithProject(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(cookie) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const otherRes = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ slug: "other", name: "Other" }),
    });
    const otherId = ((await otherRes.json()) as { id: string }).id;
    const foreign = uuidv7();
    store.putAgentSession({ id: foreign, projectId: otherId });

    const missing = await app.request(`/v1/projects/${projectId}/approvals`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ action: "constraints.apply", session_id: uuidv7() }),
    });
    expect(missing.status).toBe(400);

    const crossed = await app.request(`/v1/projects/${projectId}/approvals`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ action: "constraints.apply", session_id: foreign }),
    });
    expect(crossed.status).toBe(400);

    const local = uuidv7();
    store.putAgentSession({ id: local, projectId });
    const ok = await app.request(`/v1/projects/${projectId}/approvals`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ action: "constraints.apply", session_id: local }),
    });
    expect(ok.status).toBe(201);
    expect(await ok.json()).toMatchObject({ session_id: local });
  });

  it("returns 401 after expiry without updating last_used_at", async () => {
    const store = new MemoryAuthStore();
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const { app, cookie, projectId } = await bootstrapWithProject(store, { clock });

    const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ name: "week", ttl: "7d" }),
    });
    const created = (await minted.json()) as { id: string; token: string; expires_at: string };
    expect(created.expires_at).toBe("2026-01-08T00:00:00.000Z");

    const beforeExpiry = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${created.token}`, "content-type": "application/json" },
      body: "{}",
    });
    expect(beforeExpiry.status).toBe(200);
    const used = await store.findApiTokenById(created.id);
    expect(used?.lastUsedAt?.toISOString()).toBe(now.toISOString());

    now = new Date("2026-01-08T00:00:00.000Z");
    const expired = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { authorization: `Bearer ${created.token}`, "content-type": "application/json" },
      body: "{}",
    });
    expect(expired.status).toBe(401);
    const after = await store.findApiTokenById(created.id);
    expect(after?.lastUsedAt?.toISOString()).toBe("2026-01-01T00:00:00.000Z");
  });

  it("does not mount token-probe unless tests opt in", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });
    const boot = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const cookie = sessionCookie(boot)!;
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(cookie) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ slug: "noprobe", name: "No probe" }),
    });
    const projectId = ((await created.json()) as { id: string }).id;
    const probe = await app.request(`/v1/projects/${projectId}/token-probe`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: "{}",
    });
    expect(probe.status).toBe(404);
  });
});
