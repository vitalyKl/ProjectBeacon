import { DEFAULT_TOKEN_SCOPES, isScope, isUuid, uuidv7, type Scope } from "@beacon/shared";
import type { Hono } from "hono";

import {
  authorizeProjectActor,
  requireActor,
  requireProjectActor,
  taskStatusOnCreate,
} from "../auth/access.js";
import {
  DEFAULT_TOKEN_TTL,
  generateProjectToken,
  hashProjectToken,
  isTokenTtl,
  projectTokenDisplayPrefix,
  tokenExpiresAt,
  type TokenTtl,
} from "../auth/project-tokens.js";
import type { AuthDeps } from "../auth/routes.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { presentApproval, presentApiToken } from "./present.js";

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

function parseScopes(value: unknown): Scope[] | undefined {
  if (value === undefined) {
    return [...DEFAULT_TOKEN_SCOPES];
  }
  if (!Array.isArray(value)) {
    return undefined;
  }
  const scopes: Scope[] = [];
  for (const item of value) {
    if (typeof item !== "string" || !isScope(item)) {
      return undefined;
    }
    if (!scopes.includes(item)) {
      scopes.push(item);
    }
  }
  return scopes;
}

function parseTtl(body: Record<string, unknown> | undefined): TokenTtl | "invalid" {
  const raw = body?.["ttl"] ?? body?.["expires"];
  if (raw === undefined) {
    return DEFAULT_TOKEN_TTL;
  }
  if (typeof raw !== "string" || !isTokenTtl(raw)) {
    return "invalid";
  }
  return raw;
}

function actorUserId(actor: Awaited<ReturnType<typeof requireActor>>): string | null {
  if (isResponse(actor) || actor.kind !== "user") {
    return null;
  }
  return actor.user.id;
}

export function mountTokens(app: Hono, deps: AuthDeps): void {
  app.get("/v1/projects/:id/tokens", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "admin");
    if (isResponse(access)) {
      return access;
    }
    const items = (await deps.store.listApiTokens(access.project.id)).map((token) =>
      presentApiToken(token),
    );
    return c.json({ items, next_cursor: null });
  });

  app.post("/v1/projects/:id/tokens", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "admin");
    if (isResponse(access)) {
      return access;
    }

    const body = await readObject(c);
    const name = parseOptionalString(body?.["name"], 120);
    const scopes = parseScopes(body?.["scopes"]);
    const ttl = parseTtl(body);
    if (!name || !scopes || ttl === "invalid") {
      return errorJson(c, 400, "unauthorized", "name and valid scopes are required", {
        reason: "invalid_body",
      });
    }
    if (ttl === "none" && body?.["confirm"] !== true) {
      return errorJson(c, 400, "unauthorized", "no-expiry tokens require confirm: true", {
        reason: "confirm_required",
      });
    }

    const now = deps.clock.now();
    const secret = generateProjectToken();
    const token = await deps.store.createApiToken({
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      name,
      tokenHash: hashProjectToken(secret),
      prefix: projectTokenDisplayPrefix(secret),
      scopes,
      createdBy: actorUserId(access.actor),
      lastUsedAt: null,
      expiresAt: tokenExpiresAt(now, ttl),
      revokedAt: null,
      createdAt: now,
    });
    return c.json(presentApiToken(token, secret), 201);
  });

  app.post("/v1/tokens/:id/revoke", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "token not found");
    }
    const token = await deps.store.findApiTokenById(id);
    if (!token) {
      return errorJson(c, 404, "not_found", "token not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, token.projectId, "admin");
    if (isResponse(access)) {
      return access;
    }
    const revoked = await deps.store.revokeApiToken(token.id, deps.clock.now());
    if (!revoked) {
      return errorJson(c, 404, "not_found", "token not found");
    }
    return c.json(presentApiToken(revoked));
  });

  app.get("/v1/projects/:id/approvals", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "admin");
    if (isResponse(access)) {
      return access;
    }
    const items = (await deps.store.listApprovals(access.project.id, "pending")).map(
      presentApproval,
    );
    return c.json({ items, next_cursor: null });
  });

  app.post("/v1/projects/:id/approvals", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "admin");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const action = parseOptionalString(body?.["action"], 120);
    if (!action) {
      return errorJson(c, 400, "unauthorized", "action is required", { reason: "invalid_body" });
    }
    const payload =
      body?.["payload"] !== undefined &&
      body["payload"] !== null &&
      typeof body["payload"] === "object" &&
      !Array.isArray(body["payload"])
        ? (body["payload"] as Record<string, unknown>)
        : {};
    const sessionIdRaw = body?.["session_id"];
    let sessionId: string | null;
    if (sessionIdRaw === undefined || sessionIdRaw === null) {
      sessionId = null;
    } else if (typeof sessionIdRaw !== "string" || !isUuid(sessionIdRaw)) {
      return errorJson(c, 400, "unauthorized", "invalid session_id", { reason: "invalid_body" });
    } else {
      const session = await deps.store.findAgentSessionById(sessionIdRaw);
      if (!session || session.projectId !== access.project.id) {
        return errorJson(c, 400, "unauthorized", "invalid session_id", {
          reason: "invalid_session",
        });
      }
      sessionId = session.id;
    }
    const now = deps.clock.now();
    const approval = await deps.store.createApproval({
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      sessionId,
      action,
      payload,
      status: "pending",
      requestedAt: now,
      resolvedAt: null,
      resolvedBy: null,
    });
    return c.json(presentApproval(approval), 201);
  });

  app.post("/v1/approvals/:id/resolve", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "approval not found");
    }
    const approval = await deps.store.findApprovalById(id);
    if (!approval) {
      return errorJson(c, 404, "not_found", "approval not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, approval.projectId, "admin");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const decision = body?.["decision"];
    if (decision !== "approved" && decision !== "denied") {
      return errorJson(c, 400, "unauthorized", "decision must be approved or denied", {
        reason: "invalid_body",
      });
    }
    const resolved = await deps.store.resolveApproval(
      approval.id,
      decision,
      deps.clock.now(),
      actor.kind === "user" ? actor.user.id : null,
    );
    if (!resolved) {
      return errorJson(c, 404, "not_found", "approval not found");
    }
    return c.json(presentApproval(resolved));
  });
}

/** Test-only stand-in until task create exists (PR 07). */
export function mountTokenProbe(app: Hono, deps: AuthDeps): void {
  app.post("/v1/projects/:id/token-probe", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "project:read");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const requested = typeof body?.["status"] === "string" ? body["status"] : undefined;
    return c.json({
      actor: access.actor.kind,
      status: taskStatusOnCreate(access.actor, requested),
      scopes: access.actor.kind === "token" ? access.actor.token.scopes : null,
    });
  });
}
