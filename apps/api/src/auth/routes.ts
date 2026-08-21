import { uuidv7 } from "@beacon/shared";
import { getConnInfo } from "@hono/node-server/conninfo";
import type { Context, Hono } from "hono";

import { requireActor } from "./access.js";
import { errorJson } from "../errors.js";
import { isHostedCloneEnabled } from "../flags.js";
import { readObject } from "../http.js";
import type { Clock } from "./clock.js";
import { clearSessionCookie, readSessionCookie, writeSessionCookie } from "./cookies.js";
import { isGithubOAuthEnabled, type AuthConfig } from "./config.js";
import { exchangeGithubCode } from "./github.js";
import { hashPassword, isPasswordPolicyOk, verifyPassword } from "./password.js";
import {
  issueSession,
  lookupValidSession,
  resolveSession,
  toPublicMe,
  toPublicOrg,
  toPublicUser,
} from "./session.js";
import type { OrgStore } from "../orgs/store.js";
import type { TokenStore } from "../tokens/store.js";
import {
  BootstrapConsumedError,
  GithubIdTakenError,
  type IdentityStore,
  LoginTakenError,
  type UserRecord,
} from "./identity.js";
import { parseBearer, tokenEquals } from "./tokens.js";
import { enforceAuthAttemptLimit, type RateLimitConfig } from "./rate-limit.js";

export type AuthDeps = {
  store: IdentityStore;
  config: AuthConfig;
  clock: Clock;
  githubFetch: typeof fetch;
  rateLimits: RateLimitConfig;
};

export type AuthRouteDeps = AuthDeps & {
  store: OrgStore & TokenStore;
};

const LOGIN_TAKEN_MESSAGE = "login is already taken";

function isPlausibleIp(value: string): boolean {
  return /^[0-9.]+$/.test(value) || value.includes(":");
}

function requestIp(c: Context, trustProxy: boolean): string | null {
  if (trustProxy) {
    const forwarded = c.req.header("x-forwarded-for")?.split(",")[0]?.trim();
    const raw = forwarded || c.req.header("x-real-ip")?.trim();
    if (raw && isPlausibleIp(raw)) {
      return raw;
    }
  }

  try {
    const address = getConnInfo(c).remote.address;
    if (address && isPlausibleIp(address)) {
      return address;
    }
  } catch {
    // app.request() has no socket
  }
  return null;
}

function requestMeta(
  c: Context,
  trustProxy: boolean,
): { userAgent: string | null; ip: string | null } {
  return {
    userAgent: c.req.header("user-agent") ?? null,
    ip: requestIp(c, trustProxy),
  };
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

function newLocalUser(now: Date, login: string, passwordHash: string): UserRecord {
  return {
    id: uuidv7(now.getTime()),
    githubId: null,
    login,
    email: null,
    name: null,
    avatarUrl: null,
    passwordHash,
    createdAt: now,
    updatedAt: now,
  };
}

async function establishSession(
  c: Context,
  deps: AuthRouteDeps,
  user: UserRecord,
): Promise<Response> {
  const now = deps.clock.now();
  await deps.store.ensurePersonalOrg(user, now);
  const { token } = await issueSession(
    deps.store,
    user,
    now,
    requestMeta(c, deps.config.trustProxy),
  );
  writeSessionCookie(c, token, deps.config.secureCookies);
  return c.json(toPublicUser(user));
}

export async function loadSession(c: Context, deps: AuthDeps) {
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

export function mountAuth(app: Hono, deps: AuthRouteDeps): void {
  app.post("/v1/auth/bootstrap", async (c) => {
    const provided = parseBearer(c.req.header("authorization"));
    if (!tokenEquals(deps.config.bootstrapAdminToken, provided)) {
      return errorJson(c, 401, "unauthorized", "invalid bootstrap token");
    }

    const body = await readObject(c);
    const login = parseLogin(body?.["login"]);
    const password = parsePassword(body?.["password"]);
    if (!login || password === undefined) {
      return errorJson(c, 400, "invalid_request", "login and password are required", {
        reason: "invalid_body",
      });
    }
    if (!isPasswordPolicyOk(password)) {
      return errorJson(c, 400, "invalid_request", "password must be at least 10 characters", {
        reason: "password_policy",
      });
    }

    try {
      const user = await deps.store.createFirstUser(
        newLocalUser(deps.clock.now(), login, await hashPassword(password)),
      );
      return establishSession(c, deps, user);
    } catch (error) {
      if (error instanceof BootstrapConsumedError || error instanceof LoginTakenError) {
        return errorJson(c, 409, "bootstrap_consumed", "bootstrap has already been consumed");
      }
      throw error;
    }
  });

  app.post("/v1/auth/register", async (c) => {
    if (!deps.config.authLocal) {
      return errorJson(c, 403, "forbidden", "local registration is disabled");
    }
    if (deps.config.authLocalInviteOnly) {
      return errorJson(c, 403, "forbidden", "local registration requires an invite");
    }

    const body = await readObject(c);
    const login = parseLogin(body?.["login"]);
    const password = parsePassword(body?.["password"]);
    const limited = await enforceAuthAttemptLimit(
      c,
      deps.store,
      deps.rateLimits,
      deps.clock.now(),
      requestIp(c, deps.config.trustProxy),
      login,
    );
    if (limited) {
      return limited;
    }
    if (!login || password === undefined) {
      return errorJson(c, 400, "invalid_request", "login and password are required", {
        reason: "invalid_body",
      });
    }
    if (!isPasswordPolicyOk(password)) {
      return errorJson(c, 400, "invalid_request", "password must be at least 10 characters", {
        reason: "password_policy",
      });
    }

    try {
      const user = await deps.store.createUser(
        newLocalUser(deps.clock.now(), login, await hashPassword(password)),
      );
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
    const limited = await enforceAuthAttemptLimit(
      c,
      deps.store,
      deps.rateLimits,
      deps.clock.now(),
      requestIp(c, deps.config.trustProxy),
      login,
    );
    if (limited) {
      return limited;
    }
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
    const resolved = await lookupValidSession(deps.store, token, now);
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
      if (error instanceof GithubIdTakenError) {
        const raced = await deps.store.findUserByGithubId(exchanged.profile.id);
        if (raced) {
          return establishSession(c, deps, raced);
        }
        return errorJson(c, 401, "unauthorized", "invalid GitHub authorization code");
      }
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
    const personal = await deps.store.ensurePersonalOrg(resolved.user, deps.clock.now());
    const orgs = await deps.store.listOrgsForUser(resolved.user.id);
    if (!orgs.some((org) => org.id === personal.id)) {
      orgs.unshift(personal);
    }
    return c.json({
      ...toPublicMe(resolved.user, orgs),
      personal_org: toPublicOrg(personal),
    });
  });

  app.get("/v1/flags", async (c) => {
    const actor = await requireActor(c, deps);
    if (actor instanceof Response) {
      return actor;
    }
    return c.json({
      hosted_clone: isHostedCloneEnabled(process.env),
    });
  });
}
