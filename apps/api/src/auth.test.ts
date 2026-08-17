import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { hashSessionToken, SESSION_TTL_MS } from "./auth/tokens.js";
import { MemoryAuthStore } from "./auth/store.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    authLocal: true,
    authLocalInviteOnly: true,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    secureCookies: false,
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

async function bootstrapUser(
  store: MemoryAuthStore,
  config: AuthConfig = testConfig(),
  body: { login: string; password: string } = { login: "admin", password: STRONG_PASSWORD },
) {
  const app = createApp({ store, config, checkReady: async () => true });
  const res = await app.request("/v1/auth/bootstrap", {
    method: "POST",
    headers: {
      authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
      "content-type": "application/json",
    },
    body: JSON.stringify(body),
  });
  return { app, res, token: sessionCookie(res) };
}

describe("POST /v1/auth/bootstrap", () => {
  it("creates the first user and sets a host-only session cookie", async () => {
    const store = new MemoryAuthStore();
    const { res, token } = await bootstrapUser(store);

    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ login: "admin", email: null });
    expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/);

    const setCookie = res.headers.get("set-cookie") ?? "";
    expect(setCookie).toContain("HttpOnly");
    expect(setCookie).toMatch(/Path=\//);
    expect(setCookie).toMatch(/SameSite=Lax/i);
    expect(setCookie).toMatch(/Max-Age=1209600/);
    expect(setCookie).not.toMatch(/Domain=/i);
    expect(setCookie).not.toMatch(/Secure/i);
    expect(await store.hasAnyUser()).toBe(true);
  });

  it("returns 409 bootstrap_consumed when a user already exists", async () => {
    const store = new MemoryAuthStore();
    const first = await bootstrapUser(store);
    expect(first.res.status).toBe(200);

    const second = await bootstrapUser(store, testConfig(), {
      login: "other",
      password: STRONG_PASSWORD,
    });
    expect(second.res.status).toBe(409);
    expect(await second.res.json()).toEqual({
      error: { code: "bootstrap_consumed", message: "bootstrap has already been consumed", details: {} },
    });
  });

  it("rejects a missing or incorrect bootstrap token", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const missing = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    expect(missing.status).toBe(401);
    expect(await missing.json()).toMatchObject({ error: { code: "unauthorized" } });

    const bad = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: "Bearer not-the-token",
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    expect(bad.status).toBe(401);
    expect(await bad.json()).toMatchObject({ error: { code: "unauthorized" } });
    expect(await store.hasAnyUser()).toBe(false);
  });
});

describe("POST /v1/auth/login", () => {
  it("returns a session cookie for a valid password", async () => {
    const store = new MemoryAuthStore();
    await bootstrapUser(store);
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const res = await app.request("/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });

    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ login: "admin" });
    expect(sessionCookie(res)).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("rejects a bad password", async () => {
    const store = new MemoryAuthStore();
    await bootstrapUser(store);
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const res = await app.request("/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "admin", password: "wrong-password" }),
    });

    expect(res.status).toBe(401);
    expect(await res.json()).toEqual({
      error: { code: "unauthorized", message: "invalid login or password", details: {} },
    });
    expect(sessionCookie(res)).toBeUndefined();
  });
});

describe("POST /v1/auth/logout and GET /v1/me", () => {
  it("revokes the session so a later /me is 401", async () => {
    const store = new MemoryAuthStore();
    const { token } = await bootstrapUser(store);
    expect(token).toBeDefined();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    expect(me.status).toBe(200);
    expect(await me.json()).toMatchObject({ login: "admin" });

    const logout = await app.request("/v1/auth/logout", {
      method: "POST",
      headers: { cookie: cookieHeader(token!) },
    });
    expect(logout.status).toBe(200);

    const after = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    expect(after.status).toBe(401);
    expect(await after.json()).toMatchObject({ error: { code: "unauthorized" } });
  });
});

describe("rolling session expiry", () => {
  it("updates last_seen_at and expires_at when more than one hour has passed", async () => {
    const store = new MemoryAuthStore();
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const config = testConfig();
    const app = createApp({ store, config, clock, checkReady: async () => true });

    const boot = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(boot);
    expect(token).toBeDefined();

    const created = await store.findSessionByTokenHash(hashSessionToken(token!));
    expect(created?.lastSeenAt.toISOString()).toBe(now.toISOString());

    now = new Date("2026-01-01T00:30:00.000Z");
    const early = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    expect(early.status).toBe(200);
    const untouched = await store.findSessionByTokenHash(hashSessionToken(token!));
    expect(untouched?.lastSeenAt.toISOString()).toBe("2026-01-01T00:00:00.000Z");

    now = new Date("2026-01-01T01:00:00.001Z");
    const rolled = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    expect(rolled.status).toBe(200);
    const updated = await store.findSessionByTokenHash(hashSessionToken(token!));
    expect(updated?.lastSeenAt.toISOString()).toBe(now.toISOString());
    expect(updated?.expiresAt.getTime()).toBe(now.getTime() + SESSION_TTL_MS);
  });
});

describe("POST /v1/auth/register", () => {
  it("returns 403 when invite-only registration is not implemented", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const res = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "second", password: STRONG_PASSWORD }),
    });

    expect(res.status).toBe(403);
    expect(await res.json()).toEqual({
      error: { code: "forbidden", message: "invites land in PR 06", details: {} },
    });
  });

  it("creates a user when invite-only is off and returns 409 login_taken on collision", async () => {
    const store = new MemoryAuthStore();
    const config = testConfig({ authLocalInviteOnly: false });
    const app = createApp({ store, config, checkReady: async () => true });

    const created = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "second", password: STRONG_PASSWORD }),
    });
    expect(created.status).toBe(200);
    expect(await created.json()).toMatchObject({ login: "second" });

    const collision = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "second", password: STRONG_PASSWORD }),
    });
    expect(collision.status).toBe(409);
    expect(await collision.json()).toMatchObject({ error: { code: "login_taken" } });
  });
});

describe("POST /v1/auth/github", () => {
  it("returns 404 when GitHub OAuth is disabled", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const res = await app.request("/v1/auth/github", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ code: "abc" }),
    });

    expect(res.status).toBe(404);
    expect(await res.json()).toMatchObject({ error: { code: "not_found" } });
  });

  it("exchanges a code and 409s when the GitHub login is taken", async () => {
    const store = new MemoryAuthStore();
    await bootstrapUser(store, testConfig(), { login: "octocat", password: STRONG_PASSWORD });
    const config = testConfig({
      authGithub: true,
      githubClientId: "client",
      githubClientSecret: "secret",
    });
    const githubFetch: typeof fetch = async (input) => {
      const url = String(input);
      if (url.includes("login/oauth/access_token")) {
        return Response.json({ access_token: "gho_test" });
      }
      return Response.json({
        id: 1,
        login: "octocat",
        email: "octocat@example.com",
        name: "The Octocat",
        avatar_url: "https://example.com/a.png",
      });
    };
    const app = createApp({ store, config, githubFetch, checkReady: async () => true });

    const res = await app.request("/v1/auth/github", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ code: "good" }),
    });

    expect(res.status).toBe(409);
    expect(await res.json()).toMatchObject({ error: { code: "login_taken" } });
  });
});
