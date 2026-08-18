import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { parseGithubUserId } from "./auth/github.js";
import { hashSessionToken, SESSION_TTL_MS } from "./auth/tokens.js";
import {
  GithubIdTakenError,
  LoginTakenError,
  MemoryAuthStore,
  type UserRecord,
} from "./auth/store.js";

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
    trustProxy: false,
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

  it("lets only one concurrent bootstrap succeed", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });
    const headers = {
      authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
      "content-type": "application/json",
    };

    const [first, second] = await Promise.all([
      app.request("/v1/auth/bootstrap", {
        method: "POST",
        headers,
        body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
      }),
      app.request("/v1/auth/bootstrap", {
        method: "POST",
        headers,
        body: JSON.stringify({ login: "other", password: STRONG_PASSWORD }),
      }),
    ]);

    const statuses = [first.status, second.status].sort((a, b) => a - b);
    expect(statuses).toEqual([200, 409]);
    const consumed = first.status === 409 ? first : second;
    expect(await consumed.json()).toEqual({
      error: { code: "bootstrap_consumed", message: "bootstrap has already been consumed", details: {} },
    });
    expect(Boolean(await store.findUserByLogin("admin")) !== Boolean(await store.findUserByLogin("other"))).toBe(
      true,
    );
  });

  it("returns 400 without using forbidden for invalid bootstrap bodies", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });

    const res = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: "short" }),
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({
      error: {
        code: "unauthorized",
        message: "password must be at least 10 characters",
        details: { reason: "password_policy" },
      },
    });
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

  it("revokes an idle session without rolling expiry", async () => {
    const store = new MemoryAuthStore();
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const app = createApp({ store, config: testConfig(), clock, checkReady: async () => true });

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
    expect(created?.expiresAt.toISOString()).toBe("2026-01-15T00:00:00.000Z");

    now = new Date("2026-01-01T02:00:00.000Z");
    const logout = await app.request("/v1/auth/logout", {
      method: "POST",
      headers: { cookie: cookieHeader(token!) },
    });
    expect(logout.status).toBe(200);

    const revoked = await store.findSessionByTokenHash(hashSessionToken(token!));
    expect(revoked?.revokedAt?.toISOString()).toBe(now.toISOString());
    expect(revoked?.lastSeenAt.toISOString()).toBe("2026-01-01T00:00:00.000Z");
    expect(revoked?.expiresAt.toISOString()).toBe("2026-01-15T00:00:00.000Z");
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
      error: { code: "forbidden", message: "local registration requires an invite", details: {} },
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

  it("accepts a GitHub user id encoded as a digit string", async () => {
    const store = new MemoryAuthStore();
    const config = testConfig({
      authGithub: true,
      githubClientId: "client",
      githubClientSecret: "secret",
    });
    const githubId = "9007199254740993";
    const githubFetch: typeof fetch = async (input) => {
      const url = String(input);
      if (url.includes("login/oauth/access_token")) {
        return Response.json({ access_token: "gho_test" });
      }
      return Response.json({
        id: githubId,
        login: "huge-id",
        email: null,
        name: null,
        avatar_url: null,
      });
    };
    const app = createApp({ store, config, githubFetch, checkReady: async () => true });

    const res = await app.request("/v1/auth/github", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ code: "good" }),
    });

    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ login: "huge-id" });
    expect((await store.findUserByGithubId(BigInt(githubId)))?.login).toBe("huge-id");
  });

  it("logs in an existing GitHub user when github_id collides on create", async () => {
    const inner = new MemoryAuthStore();
    const existing: UserRecord = {
      id: "018f1e2c-3d4e-7000-8000-000000000099",
      githubId: 42n,
      login: "octocat",
      email: null,
      name: null,
      avatarUrl: null,
      passwordHash: null,
      createdAt: new Date("2026-01-01T00:00:00.000Z"),
      updatedAt: new Date("2026-01-01T00:00:00.000Z"),
    };
    await inner.createUser(existing);

    class RaceStore extends MemoryAuthStore {
      private missOnce = true;

      override async findUserByGithubId(githubId: bigint) {
        if (this.missOnce) {
          this.missOnce = false;
          return undefined;
        }
        return inner.findUserByGithubId(githubId);
      }

      override async createUser(): Promise<UserRecord> {
        throw new GithubIdTakenError();
      }
    }

    const store = new RaceStore();
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
      return Response.json({ id: 42, login: "octocat" });
    };
    const app = createApp({ store, config, githubFetch, checkReady: async () => true });

    const res = await app.request("/v1/auth/github", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ code: "good" }),
    });

    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ login: "octocat", id: existing.id });
  });
});

describe("auth store uniqueness", () => {
  it("distinguishes login collisions from github_id collisions", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-01-01T00:00:00.000Z");
    await store.createUser({
      id: "018f1e2c-3d4e-7000-8000-000000000001",
      githubId: 1n,
      login: "taken",
      email: null,
      name: null,
      avatarUrl: null,
      passwordHash: null,
      createdAt: now,
      updatedAt: now,
    });

    await expect(
      store.createUser({
        id: "018f1e2c-3d4e-7000-8000-000000000002",
        githubId: 2n,
        login: "taken",
        email: null,
        name: null,
        avatarUrl: null,
        passwordHash: null,
        createdAt: now,
        updatedAt: now,
      }),
    ).rejects.toBeInstanceOf(LoginTakenError);

    await expect(
      store.createUser({
        id: "018f1e2c-3d4e-7000-8000-000000000003",
        githubId: 1n,
        login: "other",
        email: null,
        name: null,
        avatarUrl: null,
        passwordHash: null,
        createdAt: now,
        updatedAt: now,
      }),
    ).rejects.toBeInstanceOf(GithubIdTakenError);
  });
});

describe("parseGithubUserId", () => {
  it("accepts safe numbers and digit strings and rejects junk", () => {
    expect(parseGithubUserId(1)).toBe(1n);
    expect(parseGithubUserId("9007199254740993")).toBe(9007199254740993n);
    expect(parseGithubUserId(1.5)).toBeUndefined();
    expect(parseGithubUserId(0)).toBeUndefined();
    expect(parseGithubUserId("-1")).toBeUndefined();
    expect(parseGithubUserId("1e2")).toBeUndefined();
  });
});

describe("session ip metadata", () => {
  it("ignores forwarded headers unless TRUST_PROXY is enabled", async () => {
    const store = new MemoryAuthStore();
    const untrusted = createApp({ store, config: testConfig(), checkReady: async () => true });
    const boot = await untrusted.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
        "x-forwarded-for": "203.0.113.9",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(boot);
    expect((await store.findSessionByTokenHash(hashSessionToken(token!)))?.ip).toBeNull();

    const trustedStore = new MemoryAuthStore();
    const trusted = createApp({
      store: trustedStore,
      config: testConfig({ trustProxy: true }),
      checkReady: async () => true,
    });
    const trustedBoot = await trusted.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
        "x-forwarded-for": "203.0.113.9",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const trustedToken = sessionCookie(trustedBoot);
    expect((await trustedStore.findSessionByTokenHash(hashSessionToken(trustedToken!)))?.ip).toBe("203.0.113.9");
  });
});
