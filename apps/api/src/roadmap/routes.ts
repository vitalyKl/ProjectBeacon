import { isUuid, uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import { authorizeProjectActor, requireActor, type AuthActor } from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { DependencyCycleError, VersionConflictError, type UserRecord } from "../auth/store.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { isResponse, requireProjectAccess, requireSession } from "../orgs/routes.js";
import { projectRoleAtLeast, type ProjectRecord, type ProjectRole } from "../orgs/types.js";
import { rejectAgentTerminalStatus } from "../sessions/routes.js";
import { parsePageQuery, paginateRecords } from "./page.js";
import {
  presentActivity,
  presentComment,
  presentDependency,
  presentMilestone,
  presentTask,
} from "./present.js";
import {
  isDependencyType,
  isMilestoneStatus,
  isTaskStatus,
  isTaskType,
  type DependencyType,
  type LinkedPath,
  type TaskPatch,
  type TaskRecord,
  type TaskStatus,
  type TaskType,
} from "./types.js";

function parseExpectedVersion(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isInteger(value) && value >= 1) {
    return value;
  }
  if (typeof value === "string" && /^[0-9]+$/.test(value)) {
    const parsed = Number(value);
    if (Number.isInteger(parsed) && parsed >= 1) {
      return parsed;
    }
  }
  return undefined;
}

function parsePriority(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isInteger(value)) {
    return value;
  }
  if (typeof value === "string" && /^-?[0-9]+$/.test(value)) {
    return Number(value);
  }
  return undefined;
}

function parseNullableUuid(value: unknown): string | null | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (value === null) {
    return null;
  }
  if (typeof value === "string" && isUuid(value)) {
    return value;
  }
  return undefined;
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

function parseLinkedPaths(
  value: unknown,
): { ok: true; paths: LinkedPath[] } | { ok: false; reason: "invalid" | "repo_ambiguous" } {
  if (value === undefined) {
    return { ok: true, paths: [] };
  }
  if (!Array.isArray(value)) {
    return { ok: false, reason: "invalid" };
  }
  const paths: LinkedPath[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return { ok: false, reason: "invalid" };
    }
    const record = item as Record<string, unknown>;
    const path = parseOptionalString(record["path"], 1024);
    if (!path) {
      return { ok: false, reason: "invalid" };
    }
    const repoId = record["repo_id"];
    if (repoId === undefined || repoId === null) {
      return { ok: false, reason: "repo_ambiguous" };
    }
    if (typeof repoId !== "string" || !isUuid(repoId)) {
      return { ok: false, reason: "invalid" };
    }
    paths.push({ repo_id: repoId, path });
  }
  return { ok: true, paths };
}

function parseIdempotencyKey(c: Context): string | undefined {
  const header = c.req.header("idempotency-key")?.trim();
  if (!header || header.length > 256) {
    return undefined;
  }
  return header;
}

function versionConflict(c: Context, task: TaskRecord) {
  return errorJson(c, 409, "version_conflict", "version conflict", {
    task: presentTask(task),
  });
}

async function requireTaskAccess(
  c: Context,
  deps: AuthDeps,
  user: UserRecord,
  taskId: string,
  needed: ProjectRole,
): Promise<{ task: TaskRecord; project: ProjectRecord; role: ProjectRole } | Response> {
  if (!isUuid(taskId)) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  const task = await deps.store.findTaskById(taskId);
  if (!task || task.deletedAt) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  const project = await deps.store.findProjectById(task.projectId);
  if (!project || project.deletedAt) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  const member = await deps.store.findProjectMember(project.id, user.id);
  if (!member) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  if (!projectRoleAtLeast(member.role, needed)) {
    return errorJson(c, 403, "forbidden", "insufficient project role");
  }
  return { task, project, role: member.role };
}

async function requireTaskActor(
  c: Context,
  deps: AuthDeps,
  taskId: string,
  needed: "tasks:read" | "tasks:write" | "tasks:delete",
): Promise<
  | { task: TaskRecord; project: ProjectRecord; actor: AuthActor; role: ProjectRole | null }
  | Response
> {
  const actor = await requireActor(c, deps);
  if (actor instanceof Response) {
    return actor;
  }
  if (!isUuid(taskId)) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  const task = await deps.store.findTaskById(taskId);
  if (!task || task.deletedAt) {
    return errorJson(c, 404, "not_found", "task not found");
  }
  const access = await authorizeProjectActor(c, deps, actor, task.projectId, needed);
  if (access instanceof Response) {
    return access;
  }
  return { ...access, task };
}

function actorActivity(actor: AuthActor): { actorType: string; actorId: string } {
  if (actor.kind === "token") {
    return { actorType: "token", actorId: actor.token.id };
  }
  return { actorType: "user", actorId: actor.user.id };
}

async function writeActivity(
  writer: { writeActivity: AuthDeps["store"]["writeActivity"] },
  input: {
    projectId: string;
    objectType: string;
    objectId: string;
    actorId: string;
    actorType?: string;
    verb: string;
    payload?: Record<string, unknown>;
    now: Date;
  },
): Promise<void> {
  await writer.writeActivity({
    id: uuidv7(input.now.getTime()),
    projectId: input.projectId,
    objectType: input.objectType,
    objectId: input.objectId,
    actorType: input.actorType ?? "user",
    actorId: input.actorId,
    verb: input.verb,
    payload: input.payload ?? {},
    createdAt: input.now,
  });
}

async function requireAssignee(
  c: Context,
  deps: AuthDeps,
  projectId: string,
  userId: string | null,
): Promise<Response | undefined> {
  if (!userId) {
    return undefined;
  }
  const user = await deps.store.findUserById(userId);
  if (!user) {
    return errorJson(c, 400, "unauthorized", "invalid assignee_user_id", { reason: "invalid_body" });
  }
  const member = await deps.store.findProjectMember(projectId, userId);
  if (!member) {
    return errorJson(c, 400, "unauthorized", "assignee is not a project member", {
      reason: "invalid_assignee",
    });
  }
  return undefined;
}

export function mountRoadmap(app: Hono, deps: AuthDeps): void {
  app.get("/v1/projects/:id/milestones", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listMilestones(access.project.id);
    const result = paginateRecords(records, page, (item) => item.createdAt);
    return c.json({
      items: result.items.map(presentMilestone),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/milestones", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "write");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const title = parseOptionalString(body?.["title"], 200);
    const description =
      body?.["description"] === undefined ? "" : parseText(body["description"], 8000, true);
    const statusRaw = body?.["status"] === undefined ? "open" : body["status"];
    const sortOrder = body?.["sort_order"] === undefined ? 0 : parsePriority(body["sort_order"]);
    const targetDateRaw = body?.["target_date"];
    let targetDate: string | null = null;
    if (targetDateRaw !== undefined && targetDateRaw !== null) {
      if (typeof targetDateRaw !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(targetDateRaw)) {
        return errorJson(c, 400, "unauthorized", "invalid target_date", { reason: "invalid_body" });
      }
      targetDate = targetDateRaw;
    }
    if (!title || description === undefined || typeof statusRaw !== "string" || !isMilestoneStatus(statusRaw)) {
      return errorJson(c, 400, "unauthorized", "title is required", { reason: "invalid_body" });
    }
    if (sortOrder === undefined) {
      return errorJson(c, 400, "unauthorized", "invalid sort_order", { reason: "invalid_body" });
    }

    const now = deps.clock.now();
    const milestone = await deps.store.createMilestone({
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      title,
      description,
      status: statusRaw,
      targetDate,
      sortOrder,
      createdAt: now,
    });
    await writeActivity(deps.store, {
      projectId: access.project.id,
      objectType: "milestone",
      objectId: milestone.id,
      actorId: session.user.id,
      verb: "create",
      now,
    });
    return c.json(presentMilestone(milestone), 201);
  });

  app.get("/v1/projects/:id/tasks", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listTasks(access.project.id);
    const result = paginateRecords(records, page, (item) => item.updatedAt);
    return c.json({
      items: result.items.map(presentTask),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/tasks", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "write");
    if (access instanceof Response) {
      return access;
    }

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      return errorJson(c, 400, "unauthorized", "Idempotency-Key is required", {
        reason: "missing_idempotency_key",
      });
    }
    const now = deps.clock.now();
    const body = await readObject(c);
    const title = parseOptionalString(body?.["title"], 200);
    const description =
      body?.["description"] === undefined ? "" : parseText(body["description"], 8000, true);
    const statusRaw = body?.["status"] === undefined ? "backlog" : body["status"];
    const typeRaw = body?.["type"] === undefined ? "task" : body["type"];
    const priority = body?.["priority"] === undefined ? 0 : parsePriority(body["priority"]);
    const milestoneId =
      body?.["milestone_id"] === undefined ? null : parseNullableUuid(body["milestone_id"]);
    const parentId = body?.["parent_id"] === undefined ? null : parseNullableUuid(body["parent_id"]);
    const assigneeUserId =
      body?.["assignee_user_id"] === undefined ? null : parseNullableUuid(body["assignee_user_id"]);
    const assigneeAgentName =
      body?.["assignee_agent_name"] === undefined || body["assignee_agent_name"] === null
        ? null
        : parseOptionalString(body["assignee_agent_name"], 120);
    const agentBrief =
      body?.["agent_brief"] === undefined ? "" : parseText(body["agent_brief"], 8000, true);
    const linkedPaths = parseLinkedPaths(body?.["linked_paths"]);
    if (!linkedPaths.ok) {
      if (linkedPaths.reason === "repo_ambiguous") {
        return errorJson(c, 400, "repo_ambiguous", "repo_id is required on linked_paths");
      }
      return errorJson(c, 400, "unauthorized", "invalid linked_paths", { reason: "invalid_body" });
    }

    if (
      !title ||
      description === undefined ||
      typeof statusRaw !== "string" ||
      !isTaskStatus(statusRaw) ||
      typeof typeRaw !== "string" ||
      !isTaskType(typeRaw) ||
      priority === undefined ||
      milestoneId === undefined ||
      parentId === undefined ||
      assigneeUserId === undefined ||
      assigneeAgentName === undefined ||
      agentBrief === undefined
    ) {
      return errorJson(c, 400, "unauthorized", "title is required", { reason: "invalid_body" });
    }

    if (milestoneId) {
      const milestone = await deps.store.findMilestoneById(milestoneId);
      if (!milestone || milestone.projectId !== access.project.id) {
        return errorJson(c, 400, "unauthorized", "invalid milestone_id", { reason: "invalid_body" });
      }
    }
    if (parentId) {
      const parent = await deps.store.findTaskById(parentId);
      if (!parent || parent.deletedAt || parent.projectId !== access.project.id) {
        return errorJson(c, 400, "unauthorized", "invalid parent_id", { reason: "invalid_body" });
      }
    }
    const assigneeError = await requireAssignee(c, deps, access.project.id, assigneeUserId);
    if (assigneeError) {
      return assigneeError;
    }

    const status: TaskStatus = statusRaw;
    const type: TaskType = typeRaw;
    const presented = await deps.store.withIdempotency(
      "user",
      session.user.id,
      idempotencyKey,
      now,
      async (writes) => {
        const task = await writes.createTask({
          id: uuidv7(now.getTime()),
          projectId: access.project.id,
          milestoneId,
          parentId,
          title,
          description,
          status,
          priority,
          type,
          version: 1,
          assigneeUserId,
          assigneeAgentName,
          agentBrief,
          linkedPaths: linkedPaths.paths,
          githubIssueId: null,
          lockedBySessionId: null,
          lockExpiresAt: null,
          deletedAt: null,
          createdAt: now,
          updatedAt: now,
        });
        await writeActivity(writes, {
          projectId: access.project.id,
          objectType: "task",
          objectId: task.id,
          actorId: session.user.id,
          verb: "create",
          payload: { status: task.status, type: task.type },
          now,
        });
        return presentTask(task);
      },
    );
    return c.json(presented, 201);
  });

  app.get("/v1/tasks/:id", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireTaskAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }
    return c.json(presentTask(access.task));
  });

  app.patch("/v1/tasks/:id", async (c) => {
    const access = await requireTaskActor(c, deps, c.req.param("id"), "tasks:write");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    if (!body) {
      return errorJson(c, 400, "unauthorized", "invalid body", { reason: "invalid_body" });
    }
    const expectedVersion = parseExpectedVersion(body["expected_version"]);
    if (expectedVersion === undefined) {
      return errorJson(c, 400, "unauthorized", "expected_version is required", {
        reason: "invalid_body",
      });
    }

    const patch: TaskPatch = {};
    if (body["title"] !== undefined) {
      const title = parseOptionalString(body["title"], 200);
      if (!title) {
        return errorJson(c, 400, "unauthorized", "invalid title", { reason: "invalid_body" });
      }
      patch.title = title;
    }
    if (body["description"] !== undefined) {
      const description = parseText(body["description"], 8000, true);
      if (description === undefined) {
        return errorJson(c, 400, "unauthorized", "invalid description", { reason: "invalid_body" });
      }
      patch.description = description;
    }
    if (body["status"] !== undefined) {
      if (typeof body["status"] !== "string" || !isTaskStatus(body["status"])) {
        return errorJson(c, 400, "unauthorized", "invalid status", { reason: "invalid_body" });
      }
      const terminal = rejectAgentTerminalStatus(c, access.actor, body["status"]);
      if (terminal) {
        return terminal;
      }
      patch.status = body["status"];
    }
    if (body["type"] !== undefined) {
      if (typeof body["type"] !== "string" || !isTaskType(body["type"])) {
        return errorJson(c, 400, "unauthorized", "invalid type", { reason: "invalid_body" });
      }
      patch.type = body["type"];
    }
    if (body["priority"] !== undefined) {
      const priority = parsePriority(body["priority"]);
      if (priority === undefined) {
        return errorJson(c, 400, "unauthorized", "invalid priority", { reason: "invalid_body" });
      }
      patch.priority = priority;
    }
    if (body["milestone_id"] !== undefined) {
      const milestoneId = parseNullableUuid(body["milestone_id"]);
      if (milestoneId === undefined) {
        return errorJson(c, 400, "unauthorized", "invalid milestone_id", { reason: "invalid_body" });
      }
      if (milestoneId) {
        const milestone = await deps.store.findMilestoneById(milestoneId);
        if (!milestone || milestone.projectId !== access.task.projectId) {
          return errorJson(c, 400, "unauthorized", "invalid milestone_id", { reason: "invalid_body" });
        }
      }
      patch.milestoneId = milestoneId;
    }
    if (body["parent_id"] !== undefined) {
      const parentId = parseNullableUuid(body["parent_id"]);
      if (parentId === undefined || parentId === access.task.id) {
        return errorJson(c, 400, "unauthorized", "invalid parent_id", { reason: "invalid_body" });
      }
      if (parentId) {
        const parent = await deps.store.findTaskById(parentId);
        if (!parent || parent.deletedAt || parent.projectId !== access.task.projectId) {
          return errorJson(c, 400, "unauthorized", "invalid parent_id", { reason: "invalid_body" });
        }
      }
      patch.parentId = parentId;
    }
    if (body["assignee_user_id"] !== undefined) {
      const assigneeUserId = parseNullableUuid(body["assignee_user_id"]);
      if (assigneeUserId === undefined) {
        return errorJson(c, 400, "unauthorized", "invalid assignee_user_id", {
          reason: "invalid_body",
        });
      }
      const assigneeError = await requireAssignee(c, deps, access.task.projectId, assigneeUserId);
      if (assigneeError) {
        return assigneeError;
      }
      patch.assigneeUserId = assigneeUserId;
    }
    if (body["assignee_agent_name"] !== undefined) {
      if (body["assignee_agent_name"] === null) {
        patch.assigneeAgentName = null;
      } else {
        const name = parseOptionalString(body["assignee_agent_name"], 120);
        if (!name) {
          return errorJson(c, 400, "unauthorized", "invalid assignee_agent_name", {
            reason: "invalid_body",
          });
        }
        patch.assigneeAgentName = name;
      }
    }
    if (body["agent_brief"] !== undefined) {
      const agentBrief = parseText(body["agent_brief"], 8000, true);
      if (agentBrief === undefined) {
        return errorJson(c, 400, "unauthorized", "invalid agent_brief", { reason: "invalid_body" });
      }
      patch.agentBrief = agentBrief;
    }
    if (body["linked_paths"] !== undefined) {
      const linkedPaths = parseLinkedPaths(body["linked_paths"]);
      if (!linkedPaths.ok) {
        if (linkedPaths.reason === "repo_ambiguous") {
          return errorJson(c, 400, "repo_ambiguous", "repo_id is required on linked_paths");
        }
        return errorJson(c, 400, "unauthorized", "invalid linked_paths", { reason: "invalid_body" });
      }
      patch.linkedPaths = linkedPaths.paths;
    }

    const now = deps.clock.now();
    const actor = actorActivity(access.actor);
    try {
      const updated = await deps.store.updateTask(access.task.id, expectedVersion, patch, now, {
        releaseLock: access.actor.kind === "user",
      });
      if (!updated) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      await writeActivity(deps.store, {
        projectId: access.task.projectId,
        objectType: "task",
        objectId: updated.task.id,
        actorId: actor.actorId,
        actorType: actor.actorType,
        verb: "update",
        payload: { version: updated.task.version },
        now,
      });
      if (updated.lockReleased) {
        await writeActivity(deps.store, {
          projectId: access.task.projectId,
          objectType: "task",
          objectId: updated.task.id,
          actorId: actor.actorId,
          actorType: actor.actorType,
          verb: "lock_released",
          now,
        });
      }
      return c.json(presentTask(updated.task));
    } catch (error) {
      if (error instanceof VersionConflictError) {
        return versionConflict(c, error.current);
      }
      throw error;
    }
  });

  app.delete("/v1/tasks/:id", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireTaskAccess(c, deps, session.user, c.req.param("id"), "admin");
    if (access instanceof Response) {
      return access;
    }
    const now = deps.clock.now();
    const deleted = await deps.store.softDeleteTask(access.task.id, now);
    if (!deleted) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    await writeActivity(deps.store, {
      projectId: access.task.projectId,
      objectType: "task",
      objectId: deleted.id,
      actorId: session.user.id,
      verb: "delete",
      now,
    });
    return c.json(presentTask(deleted));
  });

  app.post("/v1/tasks/:id/comments", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireTaskAccess(c, deps, session.user, c.req.param("id"), "write");
    if (access instanceof Response) {
      return access;
    }

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      return errorJson(c, 400, "unauthorized", "Idempotency-Key is required", {
        reason: "missing_idempotency_key",
      });
    }
    const now = deps.clock.now();
    const body = await readObject(c);
    const commentBody = parseText(body?.["body"], 8000);
    if (!commentBody) {
      return errorJson(c, 400, "unauthorized", "body is required", { reason: "invalid_body" });
    }
    const presented = await deps.store.withIdempotency(
      "user",
      session.user.id,
      idempotencyKey,
      now,
      async (writes) => {
        const comment = await writes.createComment({
          id: uuidv7(now.getTime()),
          taskId: access.task.id,
          authorType: "user",
          authorId: session.user.id,
          body: commentBody,
          createdAt: now,
        });
        await writeActivity(writes, {
          projectId: access.task.projectId,
          objectType: "task",
          objectId: access.task.id,
          actorId: session.user.id,
          verb: "comment",
          payload: { comment_id: comment.id },
          now,
        });
        return presentComment(comment);
      },
    );
    return c.json(presented, 201);
  });

  app.post("/v1/tasks/:id/status", async (c) => {
    const access = await requireTaskActor(c, deps, c.req.param("id"), "tasks:write");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const statusRaw = body?.["status"];
    const expectedVersion = parseExpectedVersion(body?.["expected_version"]);
    if (typeof statusRaw !== "string" || !isTaskStatus(statusRaw) || expectedVersion === undefined) {
      return errorJson(c, 400, "unauthorized", "status and expected_version are required", {
        reason: "invalid_body",
      });
    }
    const terminal = rejectAgentTerminalStatus(c, access.actor, statusRaw);
    if (terminal) {
      return terminal;
    }

    const now = deps.clock.now();
    const actor = actorActivity(access.actor);
    try {
      const updated = await deps.store.updateTask(
        access.task.id,
        expectedVersion,
        { status: statusRaw },
        now,
        { releaseLock: access.actor.kind === "user" },
      );
      if (!updated) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      await writeActivity(deps.store, {
        projectId: access.task.projectId,
        objectType: "task",
        objectId: updated.task.id,
        actorId: actor.actorId,
        actorType: actor.actorType,
        verb: "status",
        payload: { from: access.task.status, to: updated.task.status },
        now,
      });
      if (updated.lockReleased) {
        await writeActivity(deps.store, {
          projectId: access.task.projectId,
          objectType: "task",
          objectId: updated.task.id,
          actorId: actor.actorId,
          actorType: actor.actorType,
          verb: "lock_released",
          now,
        });
      }
      return c.json(presentTask(updated.task));
    } catch (error) {
      if (error instanceof VersionConflictError) {
        return versionConflict(c, error.current);
      }
      throw error;
    }
  });

  app.post("/v1/tasks/:id/dependencies", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireTaskAccess(c, deps, session.user, c.req.param("id"), "write");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const toTaskId = typeof body?.["to_task_id"] === "string" ? body["to_task_id"] : undefined;
    const typeRaw = body?.["type"];
    if (!toTaskId || !isUuid(toTaskId) || typeof typeRaw !== "string" || !isDependencyType(typeRaw)) {
      return errorJson(c, 400, "unauthorized", "to_task_id and type are required", {
        reason: "invalid_body",
      });
    }
    if (toTaskId === access.task.id) {
      return errorJson(c, 409, "dependency_cycle", "dependency would create a cycle");
    }
    const target = await deps.store.findTaskById(toTaskId);
    if (!target || target.deletedAt || target.projectId !== access.task.projectId) {
      return errorJson(c, 404, "not_found", "task not found");
    }

    const type: DependencyType = typeRaw;
    try {
      const dependency = await deps.store.addDependency({
        fromTaskId: access.task.id,
        toTaskId,
        type,
      });
      await writeActivity(deps.store, {
        projectId: access.task.projectId,
        objectType: "task",
        objectId: access.task.id,
        actorId: session.user.id,
        verb: "update",
        payload: { dependency: presentDependency(dependency) },
        now: deps.clock.now(),
      });
      return c.json(presentDependency(dependency), 201);
    } catch (error) {
      if (error instanceof DependencyCycleError) {
        return errorJson(c, 409, "dependency_cycle", "dependency would create a cycle");
      }
      throw error;
    }
  });

  app.get("/v1/projects/:id/activity", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const objectType = c.req.query("object_type");
    const objectId = c.req.query("object_id");
    const records = await deps.store.listActivity(access.project.id, {
      objectType: objectType || undefined,
      objectId: objectId || undefined,
    });
    const result = paginateRecords(records, page, (item) => item.createdAt);
    return c.json({
      items: result.items.map(presentActivity),
      next_cursor: result.next_cursor,
    });
  });
}
