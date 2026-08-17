import { uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import { errorJson } from "../errors.js";
import type { Clock } from "./clock.js";
import { clearSessionCookie, readSessionCookie, writeSessionCookie } from "./cookies.js";
import { isGithubOAuthEnabled, type AuthConfig } from "./config.js";
import { exchangeGithubCode } from "./github.js";
import { hashPassword, isPasswordPolicyOk, verifyPassword } from "./password.js";
import { issueSession, resolveSession, toPublicUser } from "./session.js";
import { LoginTakenError, type AuthStore, type UserRecord } from "./store.js";
import { parseBearer, tokenEquals } from "./tokens.js";

export type AuthDeps = {
  store: AuthStore;
  config: AuthConfig;
  clock: Clock;
  githubFetch: typeof fetch;
};

const LOGIN_TAKEN_MESSAGE = "login is already taken";

function requestMeta(c: Context): { userAgent: string | null; ip: string | null } {
  const forwarded = c.req.header("x-forwarded-for");
  const raw = forwarded?.split(",")[0]?.trim() || c.req.header("x-real-ip") || undefined;
  const ip = raw && (/^[0-9.]+$/.test(raw) || raw.includes(":")) ? raw : null;
  return {
    userAgent: c.req.header("user-agent") ?? null,
    ip,
  };
}

async function readObject(c: Context): Promise<Record<string, unknown> | undefined> {
  try {
    const body: unknown = await c.req.json();
    if (body === null || typeof body !== "object" || Array.isArray(body)) {
      return undefined;
    }
    return body as Record<string, unknown>;
  } catch {
    return undefined;
  }
}

function parseLogin(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const login = value.trim();
  if (login.length < 1 || login.length > 64 || /\s/.test(login)) {
    return undefined;
  }
  return login;
}

function parsePassword(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

async function createLocalUser(
  store: AuthStore,
  now: Date,
  login: string,
  password: string,
): Promise<UserRecord> {
  return store.createUser({
    id: uuidv7(now.getTime()),
    githubId: null,
    login,
    email: null,
    name: null,
    avatarUrl: null,
    passwordHash: await hashPassword(password),
    createdAt: now,
    updatedAt: now,
  });
}

async function establishSession(
  c: Context,
  deps: AuthDeps,
  user: UserRecord,
): Promise<Response> {
  const now = deps.clock.now();
  const { token } = await issueSession(deps.store, user, now, requestMeta(c));
  writeSessionCookie(c, token, deps.config.secureCookies);
  return c.json(toPublicUser(user));
}

async function loadSession(c: Context, deps: AuthDeps) {
  const token = readSessionCookie(c);
  const resolved = await resolveSession(deps.store, token, deps.clock.now());
  if (!resolved) {
    return undefined;
  }
  if (resolved.rolled && token) {
    writeSessionCookie(c, token, deps.config.secureCookies);
  }
  return resolved;
}

export function mountAuth(app: Hono, deps: AuthDeps): void {
  app.post("/v1/auth/bootstrap", async (c) => {
    const provided = parseBearer(c.req.header("authorization"));
    if (!tokenEquals(deps.config.bootstrapAdminToken, provided)) {
      return errorJson(c, 401, "unauthorized", "invalid bootstrap token");
    }
    if (await deps.store.hasAnyUser()) {
      return errorJson(c, 409, "bootstrap_consumed", "bootstrap has already been consumed");
    }

    const body = await readObject(c);
    const login = parseLogin(body?.["login"]);
    const password = parsePassword(body?.["password"]);
    if (!login || password === undefined) {
      return errorJson(c, 400, "forbidden", "login and password are required");
    }
    if (!isPasswordPolicyOk(password)) {
      return errorJson(c, 400, "forbidden", "password must be at least 10 characters");
    }

    try {
      const user = await createLocalUser(deps.store, deps.clock.now(), login, password);
      return establishSession(c, deps, user);
    } catch (error) {
      if (error instanceof LoginTakenError) {
        return errorJson(c, 409, "login_taken", LOGIN_TAKEN_MESSAGE);
      }
      throw error;
    }
  });

  app.post("/v1/auth/register", async (c) => {
    if (!deps.config.authLocal) {
      return errorJson(c, 403, "forbidden", "local registration is disabled");
    }
    if (deps.config.authLocalInviteOnly) {
      return errorJson(c, 403, "forbidden", "invites land in PR 06");
    }

    const body = await readObject(c);
    const login = parseLogin(body?.["login"]);
    const password = parsePassword(body?.["password"]);
    if (!login || password === undefined) {
      return errorJson(c, 400, "forbidden", "login and password are required");
    }
    if (!isPasswordPolicyOk(password)) {
      return errorJson(c, 400, "forbidden", "password must be at least 10 characters");
    }

    try {
      const user = await createLocalUser(deps.store, deps.clock.now(), login, password);
      return establishSession(c, deps, user);
    } catch (error) {
      if (error instanceof LoginTakenError) {
        return errorJson(c, 409, "login_taken", LOGIN_TAKEN_MESSAGE);
      }
      throw error;
    }
  });

  app.post("/v1/auth/login", async (c) => {
    const body = await readObject(c);
    const login = parseLogin(body?.["login"]);
    const password = parsePassword(body?.["password"]);
    if (!login || password === undefined) {
      return errorJson(c, 401, "unauthorized", "invalid login or password");
    }

    const user = await deps.store.findUserByLogin(login);
    if (!user || !user.passwordHash) {
      return errorJson(c, 401, "unauthorized", "invalid login or password");
    }
    if (!(await verifyPassword(user.passwordHash, password))) {
      return errorJson(c, 401, "unauthorized", "invalid login or password");
    }

    return establishSession(c, deps, user);
  });

  app.post("/v1/auth/logout", async (c) => {
    const token = readSessionCookie(c);
    const now = deps.clock.now();
    const resolved = await resolveSession(deps.store, token, now);
    if (resolved) {
      await deps.store.revokeSession(resolved.session.id, now);
    }
    clearSessionCookie(c, deps.config.secureCookies);
    return c.json({ ok: true });
  });

  app.post("/v1/auth/github", async (c) => {
    if (!isGithubOAuthEnabled(deps.config)) {
      return errorJson(c, 404, "not_found", "GitHub OAuth is disabled");
    }

    const body = await readObject(c);
    const code = typeof body?.["code"] === "string" ? body["code"].trim() : "";
    if (!code) {
      return errorJson(c, 401, "unauthorized", "invalid GitHub authorization code");
    }

    const exchanged = await exchangeGithubCode(
      code,
      deps.config.githubClientId ?? "",
      deps.config.githubClientSecret ?? "",
      deps.githubFetch,
    );
    if (!exchanged.ok) {
      return errorJson(c, 401, "unauthorized", "invalid GitHub authorization code");
    }

    const existing = await deps.store.findUserByGithubId(exchanged.profile.id);
    if (existing) {
      return establishSession(c, deps, existing);
    }

    const now = deps.clock.now();
    try {
      const user = await deps.store.createUser({
        id: uuidv7(now.getTime()),
        githubId: exchanged.profile.id,
        login: exchanged.profile.login,
        email: exchanged.profile.email,
        name: exchanged.profile.name,
        avatarUrl: exchanged.profile.avatarUrl,
        passwordHash: null,
        createdAt: now,
        updatedAt: now,
      });
      return establishSession(c, deps, user);
    } catch (error) {
      if (error instanceof LoginTakenError) {
        return errorJson(c, 409, "login_taken", LOGIN_TAKEN_MESSAGE);
      }
      throw error;
    }
  });

  app.get("/v1/me", async (c) => {
    const resolved = await loadSession(c, deps);
    if (!resolved) {
      return errorJson(c, 401, "unauthorized", "authentication required");
    }
    return c.json(toPublicUser(resolved.user));
  });
}
