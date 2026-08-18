import { isUuid, uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import {
  authorizeProjectActor,
  constraintStatusOnCreate,
  decisionStatusOnCreate,
  requireActor,
  requireProjectActor,
} from "../auth/access.js";
import type { AuthActor } from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentApproval } from "../tokens/present.js";
import {
  presentConstraintRecord,
  presentDecisionRecord,
} from "./present.js";
import {
  isConstraintKind,
  isConstraintStatus,
  isDecisionStatus,
  type ConstraintKind,
  type ConstraintStatus,
  type DecisionPathLink,
  type DecisionStatus,
} from "./types.js";

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

function parseIdempotencyKey(c: Context): string | undefined {
  const header = c.req.header("idempotency-key")?.trim();
  if (!header || header.length > 256) {
    return undefined;
  }
  return header;
}

function parseText(value: unknown, max: number, allowEmpty = false): string | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== "string" || value.length > max) {
    return undefined;
  }
  if (!allowEmpty && value.trim().length === 0) {
    return undefined;
  }
  return allowEmpty ? value : value.trim();
}

function parseRelatedTaskIds(
  value: unknown,
): { ok: true; ids: string[] } | { ok: false } {
  if (value === undefined) {
    return { ok: true, ids: [] };
  }
  if (!Array.isArray(value)) {
    return { ok: false };
  }
  const ids: string[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    if (typeof item !== "string" || !isUuid(item)) {
      return { ok: false };
    }
    if (seen.has(item)) {
      continue;
    }
    seen.add(item);
    ids.push(item);
  }
  return { ok: true, ids };
}

function parseRelatedPaths(
  value: unknown,
): { ok: true; paths: DecisionPathLink[] } | { ok: false } {
  if (value === undefined) {
    return { ok: true, paths: [] };
  }
  if (!Array.isArray(value)) {
    return { ok: false };
  }
  const paths: DecisionPathLink[] = [];
  const seen = new Set<string>();
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return { ok: false };
    }
    const record = item as Record<string, unknown>;
    const path = parseOptionalString(record["path"], 1024);
    const repoId = record["repo_id"];
    if (!path || typeof repoId !== "string" || !isUuid(repoId)) {
      return { ok: false };
    }
    const key = `${repoId}:${path}`;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    paths.push({ repoId, path });
  }
  return { ok: true, paths };
}

function actorRef(actor: AuthActor): { type: string; id: string } {
  if (actor.kind === "token") {
    return { type: "agent", id: actor.token.id };
  }
  return { type: "user", id: actor.user.id };
}

function idempotencyActor(actor: AuthActor): { type: "token" | "user"; id: string } {
  if (actor.kind === "token") {
    return { type: "token", id: actor.token.id };
  }
  return { type: "user", id: actor.user.id };
}

function wantsApproval(body: Record<string, unknown> | undefined): boolean {
  if (!body) {
    return false;
  }
  return body["request_approval"] === true || body["create_approval"] === true;
}

export function mountDecisions(app: Hono, deps: AuthDeps): void {
  app.get("/v1/projects/:id/decisions", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "decisions:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listDecisions(access.project.id);
    const result = paginateRecords(records, page, (item) => item.createdAt);
    return c.json({
      items: result.items.map(presentDecisionRecord),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/decisions", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "decisions:write");
    if (isResponse(access)) {
      return access;
    }

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      return errorJson(c, 400, "unauthorized", "Idempotency-Key is required", {
        reason: "missing_idempotency_key",
      });
    }

    const body = await readObject(c);
    const title = parseOptionalString(body?.["title"], 200);
    const contextText = parseText(body?.["context"], 8000);
    const decisionText = parseText(body?.["decision"], 8000);
    const consequences =
      body?.["consequences"] === undefined ? "" : parseText(body["consequences"], 8000, true);
    const statusRaw = decisionStatusOnCreate(
      access.actor,
      typeof body?.["status"] === "string" ? body["status"] : undefined,
    );
    const relatedTasks = parseRelatedTaskIds(body?.["related_task_ids"]);
    const relatedPaths = parseRelatedPaths(body?.["related_paths"]);

    if (
      !title ||
      !contextText ||
      !decisionText ||
      consequences === undefined ||
      !isDecisionStatus(statusRaw) ||
      !relatedTasks.ok ||
      !relatedPaths.ok
    ) {
      return errorJson(c, 400, "unauthorized", "title, context, and decision are required", {
        reason: "invalid_body",
      });
    }

    for (const taskId of relatedTasks.ids) {
      const task = await deps.store.findTaskById(taskId);
      if (!task || task.deletedAt || task.projectId !== access.project.id) {
        return errorJson(c, 400, "unauthorized", "invalid related_task_ids", {
          reason: "invalid_related_task",
        });
      }
    }
    for (const path of relatedPaths.paths) {
      const repo = await deps.store.findProjectRepo(path.repoId);
      if (!repo || repo.projectId !== access.project.id) {
        return errorJson(c, 400, "unauthorized", "invalid related_paths", {
          reason: "invalid_related_path",
        });
      }
    }

    const now = deps.clock.now();
    const actor = actorRef(access.actor);
    const idem = idempotencyActor(access.actor);
    const status: DecisionStatus = statusRaw;
    const presented = await deps.store.withIdempotency(
      idem.type,
      idem.id,
      idempotencyKey,
      now,
      async (writes) => {
        const decision = await writes.createDecision({
          id: uuidv7(now.getTime()),
          projectId: access.project.id,
          title,
          status,
          context: contextText,
          decision: decisionText,
          consequences,
          createdByType: actor.type,
          createdById: actor.id,
          supersededBy: null,
          createdAt: now,
          relatedPaths: relatedPaths.paths,
          relatedTaskIds: relatedTasks.ids,
        });
        await writes.writeActivity({
          id: uuidv7(now.getTime()),
          projectId: access.project.id,
          objectType: "decision",
          objectId: decision.id,
          actorType: actor.type,
          actorId: actor.id,
          verb: "create",
          payload: { status: decision.status },
          createdAt: now,
        });
        return presentDecisionRecord(decision);
      },
    );
    return c.json(presented, 201);
  });

  app.get("/v1/projects/:id/constraints", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "decisions:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listConstraints(access.project.id);
    const result = paginateRecords(records, page, (item) => item.createdAt);
    return c.json({
      items: result.items.map(presentConstraintRecord),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/constraints", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "decisions:write");
    if (isResponse(access)) {
      return access;
    }

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      return errorJson(c, 400, "unauthorized", "Idempotency-Key is required", {
        reason: "missing_idempotency_key",
      });
    }

    const body = await readObject(c);
    const kindRaw = body?.["kind"];
    const constraintBody = parseText(body?.["body"], 8000);
    const scopePath =
      body?.["scope_path"] === undefined ? "" : parseText(body["scope_path"], 1024, true);
    const statusRaw = constraintStatusOnCreate(
      access.actor,
      typeof body?.["status"] === "string" ? body["status"] : undefined,
    );

    if (
      typeof kindRaw !== "string" ||
      !isConstraintKind(kindRaw) ||
      !constraintBody ||
      scopePath === undefined ||
      !isConstraintStatus(statusRaw)
    ) {
      return errorJson(c, 400, "unauthorized", "kind and body are required", {
        reason: "invalid_body",
      });
    }

    const now = deps.clock.now();
    const actor = actorRef(access.actor);
    const idem = idempotencyActor(access.actor);
    const kind: ConstraintKind = kindRaw;
    const status: ConstraintStatus = statusRaw;
    const presented = await deps.store.withIdempotency(
      idem.type,
      idem.id,
      idempotencyKey,
      now,
      async (writes) => {
        const constraint = await writes.createConstraint({
          id: uuidv7(now.getTime()),
          projectId: access.project.id,
          kind,
          body: constraintBody,
          scopePath,
          status,
          createdAt: now,
        });
        await writes.writeActivity({
          id: uuidv7(now.getTime()),
          projectId: access.project.id,
          objectType: "constraint",
          objectId: constraint.id,
          actorType: actor.type,
          actorId: actor.id,
          verb: "create",
          payload: { kind: constraint.kind, status: constraint.status },
          createdAt: now,
        });
        return presentConstraintRecord(constraint);
      },
    );
    return c.json(presented, 201);
  });

  app.post("/v1/constraints/:id/apply", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "constraint not found");
    }
    const constraint = await deps.store.findConstraintById(id);
    if (!constraint) {
      return errorJson(c, 404, "not_found", "constraint not found");
    }

    const memberAccess = await authorizeProjectActor(
      c,
      deps,
      actor,
      constraint.projectId,
      "decisions:read",
    );
    if (isResponse(memberAccess)) {
      return memberAccess;
    }

    const applyAccess = await authorizeProjectActor(
      c,
      deps,
      actor,
      constraint.projectId,
      "constraints:apply",
    );
    if (isResponse(applyAccess)) {
      if (applyAccess.status !== 403) {
        return applyAccess;
      }
      const body = await readObject(c);
      if (actor.kind === "token" && wantsApproval(body)) {
        const now = deps.clock.now();
        const approval = await deps.store.createApproval({
          id: uuidv7(now.getTime()),
          projectId: constraint.projectId,
          sessionId: null,
          action: "constraints.apply",
          payload: { constraint_id: constraint.id },
          status: "pending",
          requestedAt: now,
          resolvedAt: null,
          resolvedBy: null,
        });
        return errorJson(c, 403, "forbidden", "constraint apply requires approval", {
          approval: presentApproval(approval),
        });
      }
      return applyAccess;
    }

    if (constraint.status === "active") {
      return c.json(presentConstraintRecord(constraint));
    }
    if (constraint.status !== "proposed") {
      return errorJson(c, 400, "unauthorized", "constraint is not proposed", {
        reason: "invalid_status",
      });
    }

    const now = deps.clock.now();
    const applied = await deps.store.applyConstraint(constraint.id, now);
    if (!applied) {
      return errorJson(c, 400, "unauthorized", "constraint is not proposed", {
        reason: "invalid_status",
      });
    }
    const actorIds = actorRef(actor);
    await deps.store.writeActivity({
      id: uuidv7(now.getTime()),
      projectId: applied.projectId,
      objectType: "constraint",
      objectId: applied.id,
      actorType: actorIds.type,
      actorId: actorIds.id,
      verb: "apply",
      payload: { status: applied.status },
      createdAt: now,
    });
    return c.json(presentConstraintRecord(applied));
  });
}
