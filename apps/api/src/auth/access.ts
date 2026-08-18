import { isUuid, type Scope } from "@beacon/shared";
import type { Context } from "hono";

import { errorJson } from "../errors.js";
import type { ProjectRecord, ProjectRole } from "../orgs/types.js";
import type { TokenRecord } from "../tokens/types.js";
import { parseBearer, tokenEquals } from "./tokens.js";
import { hashProjectToken, isProjectTokenFormat } from "./project-tokens.js";
import { enforceRateLimit } from "./rate-limit.js";
import { loadSession, type AuthDeps } from "./routes.js";
import type { UserRecord } from "./store.js";

export type AuthActor =
  { kind: "user"; user: UserRecord } | { kind: "token"; token: TokenRecord } | { kind: "worker" };

export const WORKER_ACTOR_ID = "00000000-0000-0000-0000-000000000001";

const READ_SCOPES: readonly Scope[] = [
  "project:read",
  "context:read",
  "tasks:read",
  "decisions:read",
  "code:read",
];

const WRITE_SCOPES: readonly Scope[] = [
  ...READ_SCOPES,
  "context:write",
  "tasks:write",
  "decisions:write",
  "sessions:write",
];

export function scopesForRole(role: ProjectRole): readonly Scope[] {
  if (role === "admin") {
    return [
      ...WRITE_SCOPES,
      "project:write",
      "tasks:delete",
      "constraints:apply",
      "integrations:write",
      "admin",
    ];
  }
  if (role === "write") {
    return WRITE_SCOPES;
  }
  return READ_SCOPES;
}

export function tokenHasScope(token: TokenRecord, scope: Scope): boolean {
  return token.scopes.includes("admin") || token.scopes.includes(scope);
}

export function isAdminActor(actor: AuthActor, role?: ProjectRole | null): boolean {
  if (actor.kind === "worker") {
    return true;
  }
  if (actor.kind === "token") {
    return actor.token.scopes.includes("admin");
  }
  return role === "admin";
}

/** Non-admin tokens may only create tasks in backlog. */
export function taskStatusOnCreate(actor: AuthActor, requested: string | undefined): string {
  if (actor.kind === "token" && !actor.token.scopes.includes("admin")) {
    return "backlog";
  }
  return requested && requested.length > 0 ? requested : "backlog";
}

/** Non-admin tokens may only record decisions as proposed. */
export function decisionStatusOnCreate(actor: AuthActor, requested: string | undefined): string {
  if (actor.kind === "token" && !actor.token.scopes.includes("admin")) {
    return "proposed";
  }
  return requested && requested.length > 0 ? requested : "proposed";
}

/** Non-admin tokens may only create constraints as proposed. */
export function constraintStatusOnCreate(actor: AuthActor, requested: string | undefined): string {
  if (actor.kind === "token" && !actor.token.scopes.includes("admin")) {
    return "proposed";
  }
  return requested && requested.length > 0 ? requested : "proposed";
}

export function actorHasCapability(
  actor: AuthActor,
  needed: Scope,
  role?: ProjectRole | null,
): boolean {
  if (actor.kind === "worker") {
    return true;
  }
  if (actor.kind === "token") {
    return tokenHasScope(actor.token, needed);
  }
  if (!role) {
    return false;
  }
  return scopesForRole(role).includes(needed);
}

export function actorIdempotencyRef(actor: AuthActor): { type: "token" | "user"; id: string } {
  if (actor.kind === "user") {
    return { type: "user", id: actor.user.id };
  }
  if (actor.kind === "token") {
    return { type: "token", id: actor.token.id };
  }
  return { type: "token", id: WORKER_ACTOR_ID };
}

export function actorActivityRef(actor: AuthActor): { type: string; id: string } {
  if (actor.kind === "user") {
    return { type: "user", id: actor.user.id };
  }
  if (actor.kind === "token") {
    return { type: "token", id: actor.token.id };
  }
  return { type: "system", id: WORKER_ACTOR_ID };
}

function isResponse(value: AuthActor | Response): value is Response {
  return value instanceof Response;
}

export async function requireActor(c: Context, deps: AuthDeps): Promise<AuthActor | Response> {
  const authorization = c.req.header("authorization");
  if (authorization !== undefined) {
    const bearer = parseBearer(authorization);
    if (deps.config.workerToken && tokenEquals(deps.config.workerToken, bearer)) {
      const actor: AuthActor = { kind: "worker" };
      const limited = await enforceRateLimit(
        c,
        deps.store,
        "token",
        WORKER_ACTOR_ID,
        deps.rateLimits,
        deps.clock.now(),
        "overall",
      );
      if (limited) {
        return limited;
      }
      return actor;
    }
    if (!bearer || !isProjectTokenFormat(bearer)) {
      return errorJson(c, 401, "unauthorized", "invalid token");
    }
    const token = await deps.store.findApiTokenByHash(hashProjectToken(bearer));
    const now = deps.clock.now();
    if (
      !token ||
      token.revokedAt ||
      (token.expiresAt !== null && token.expiresAt.getTime() <= now.getTime())
    ) {
      return errorJson(c, 401, "unauthorized", "invalid token");
    }
    await deps.store.touchApiToken(token.id, now);
    token.lastUsedAt = now;
    const actor: AuthActor = { kind: "token", token };
    const limited = await enforceRateLimit(
      c,
      deps.store,
      "token",
      token.id,
      deps.rateLimits,
      now,
      "overall",
    );
    if (limited) {
      return limited;
    }
    return actor;
  }

  const resolved = await loadSession(c, deps);
  if (!resolved) {
    return errorJson(c, 401, "unauthorized", "authentication required");
  }
  await deps.store.ensurePersonalOrg(resolved.user, deps.clock.now());
  const actor: AuthActor = { kind: "user", user: resolved.user };
  const limited = await enforceRateLimit(
    c,
    deps.store,
    "user",
    resolved.user.id,
    deps.rateLimits,
    deps.clock.now(),
    "overall",
  );
  if (limited) {
    return limited;
  }
  return actor;
}

export async function authorizeProjectActor(
  c: Context,
  deps: AuthDeps,
  actor: AuthActor,
  projectId: string,
  needed: Scope,
): Promise<{ project: ProjectRecord; actor: AuthActor; role: ProjectRole | null } | Response> {
  if (!isUuid(projectId)) {
    return errorJson(c, 404, "not_found", "project not found");
  }
  const project = await deps.store.findProjectById(projectId);
  if (!project || project.deletedAt) {
    return errorJson(c, 404, "not_found", "project not found");
  }

  if (actor.kind === "worker") {
    return { project, actor, role: "admin" };
  }

  if (actor.kind === "token") {
    if (actor.token.projectId !== project.id) {
      return errorJson(c, 404, "not_found", "project not found");
    }
    if (!actorHasCapability(actor, needed)) {
      return errorJson(c, 403, "forbidden", "insufficient token scope");
    }
    return { project, actor, role: null };
  }

  const member = await deps.store.findProjectMember(project.id, actor.user.id);
  if (!member) {
    return errorJson(c, 404, "not_found", "project not found");
  }
  if (!actorHasCapability(actor, needed, member.role)) {
    return errorJson(c, 403, "forbidden", "insufficient project role");
  }
  return { project, actor, role: member.role };
}

export async function requireProjectActor(
  c: Context,
  deps: AuthDeps,
  projectId: string,
  needed: Scope,
): Promise<{ project: ProjectRecord; actor: AuthActor; role: ProjectRole | null } | Response> {
  const actor = await requireActor(c, deps);
  if (isResponse(actor)) {
    return actor;
  }
  return authorizeProjectActor(c, deps, actor, projectId, needed);
}
