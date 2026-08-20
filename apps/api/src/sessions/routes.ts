import { isUuid, uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import {
  actorActivityRef,
  actorHasCapability,
  authorizeProjectActor,
  isAdminActor,
  requireActor,
  requireProject,
  type AuthActor,
} from "../auth/access.js";
import type { AccessDeps } from "../auth/access.js";
import type { ContextStore } from "../context/store.js";
import type { RepoStore } from "../repos/store.js";
import type { RoadmapStore } from "../roadmap/store.js";
import { compileProjectBrief } from "../context/compile-brief.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import {
  isResponse,
  parseIdempotencyKey,
  parseLinkedPaths,
  parsePageQuery,
  writeActivity,
} from "../http/parse.js";
import { paginateRecords } from "../roadmap/page.js";
import { presentTask } from "../roadmap/present.js";
import { type TaskRecord } from "../roadmap/types.js";
import { presentHandoffResource, presentSession } from "./present.js";
import {
  HANDOFF_SUMMARY_MIN,
  isAgentHost,
  isFinishWorkStatus,
  isTerminalTaskStatus,
  lockExpiresAt,
  type AgentHost,
  InvalidReferenceError,
  SessionNotActiveError,
  TaskLockedError,
  type FinishWorkStatus,
} from "./types.js";
import type { WorkStore } from "./store.js";

function parseBoolean(value: unknown): boolean | undefined {
  if (value === undefined) {
    return false;
  }
  if (typeof value === "boolean") {
    return value;
  }
  return undefined;
}

function parseBudgetTokens(value: unknown): number | undefined | "invalid" {
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === "number" && Number.isInteger(value) && value > 0) {
    return value;
  }
  return "invalid";
}

function parseOpenQuestions(value: unknown): string[] | undefined {
  if (value === undefined) {
    return [];
  }
  if (!Array.isArray(value)) {
    return undefined;
  }
  const questions: string[] = [];
  for (const item of value) {
    if (typeof item !== "string") {
      return undefined;
    }
    const trimmed = item.trim();
    if (trimmed.length === 0 || trimmed.length > 800) {
      return undefined;
    }
    questions.push(trimmed);
  }
  return questions;
}

function actorRef(actor: AuthActor): { type: string; id: string } {
  return actorActivityRef(actor);
}

function defaultAgent(actor: AuthActor): { name: string; host: AgentHost } {
  if (actor.kind === "token") {
    return { name: actor.token.name, host: "custom" };
  }
  if (actor.kind === "user") {
    return { name: actor.user.login, host: "custom" };
  }
  return { name: "worker", host: "custom" };
}

function parseAgent(
  body: Record<string, unknown> | undefined,
  actor: AuthActor,
): { name: string; host: AgentHost } | undefined {
  const fallback = defaultAgent(actor);
  const raw = body?.["agent"];
  if (raw === undefined) {
    const name = parseOptionalString(body?.["agent_name"], 120) ?? fallback.name;
    const hostRaw = body?.["agent_host"];
    if (hostRaw === undefined) {
      return { name, host: fallback.host };
    }
    if (typeof hostRaw !== "string" || !isAgentHost(hostRaw)) {
      return undefined;
    }
    return { name, host: hostRaw };
  }
  if (raw === null || typeof raw !== "object" || Array.isArray(raw)) {
    return undefined;
  }
  const record = raw as Record<string, unknown>;
  const name = parseOptionalString(record["name"], 120) ?? fallback.name;
  const hostRaw = record["host"];
  if (hostRaw === undefined) {
    return { name, host: fallback.host };
  }
  if (typeof hostRaw !== "string" || !isAgentHost(hostRaw)) {
    return undefined;
  }
  return { name, host: hostRaw };
}

function taskLocked(c: Context, task: TaskRecord) {
  return errorJson(c, 409, "task_locked", "task is locked by another session", {
    task: presentTask(task),
  });
}

export type SessionDeps = AccessDeps & {
  store: WorkStore & RoadmapStore & ContextStore & RepoStore;
};

export function mountSessions(app: Hono, deps: SessionDeps): void {
  app.get("/v1/projects/:id/sessions", async (c) => {
    const access = await requireProject(c, deps, c.req.param("id"), "project:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listAgentSessions(access.project.id);
    const result = paginateRecords(records, page, (item) => item.startedAt);
    return c.json({
      items: result.items.map(presentSession),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/sessions", async (c) => {
    const access = await requireProject(c, deps, c.req.param("id"), "sessions:write");
    if (isResponse(access)) {
      return access;
    }

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      return errorJson(c, 400, "invalid_request", "Idempotency-Key is required", {
        reason: "missing_idempotency_key",
      });
    }

    const body = await readObject(c);
    const taskIdRaw = body?.["task_id"];
    if (typeof taskIdRaw !== "string" || !isUuid(taskIdRaw)) {
      return errorJson(c, 400, "invalid_request", "task_id is required", { reason: "invalid_body" });
    }
    const steal = parseBoolean(body?.["steal"]);
    if (steal === undefined) {
      return errorJson(c, 400, "invalid_request", "invalid steal", { reason: "invalid_body" });
    }
    if (steal && !actorHasCapability(access.actor, "tasks:write", access.role)) {
      return errorJson(c, 403, "forbidden", "steal requires tasks:write");
    }
    const budgetTokens = parseBudgetTokens(body?.["budget_tokens"]);
    if (budgetTokens === "invalid") {
      return errorJson(c, 400, "invalid_request", "invalid budget_tokens", { reason: "invalid_body" });
    }
    const path = body?.["path"] === undefined ? undefined : parseOptionalString(body["path"], 1024);
    if (body?.["path"] !== undefined && path === undefined) {
      return errorJson(c, 400, "invalid_request", "invalid path", { reason: "invalid_body" });
    }
    const agent = parseAgent(body, access.actor);
    if (!agent) {
      return errorJson(c, 400, "invalid_request", "invalid agent", { reason: "invalid_body" });
    }

    const task = await deps.store.findTaskById(taskIdRaw);
    if (!task || task.deletedAt || task.projectId !== access.project.id) {
      return errorJson(c, 404, "not_found", "task not found");
    }

    const now = deps.clock.now();
    const compiled = await compileProjectBrief(
      deps.store,
      access.project,
      {
        project_id: access.project.id,
        path,
        task_id: task.id,
        budget_tokens: budgetTokens,
      },
      now,
    );
    if (!compiled.ok) {
      return errorJson(c, 404, "not_found", "task not found");
    }

    const actor = actorRef(access.actor);
    const sessionId = uuidv7(now.getTime());
    const lockExpires = lockExpiresAt(now);
    try {
      const presented = await deps.store.withIdempotency(
        actor.type === "token" ? "token" : "user",
        actor.id,
        idempotencyKey,
        now,
        async () => {
          const started = await deps.store.startWork({
            session: {
              id: sessionId,
              projectId: access.project.id,
              taskId: task.id,
              tokenId: access.actor.kind === "token" ? access.actor.token.id : null,
              agentName: agent.name,
              agentHost: agent.host,
              status: "active",
              contextRevisionId: compiled.compiled.brief.revision_id,
              startedAt: now,
              finishedAt: null,
              lockExpiresAt: lockExpires,
              lastHeartbeatAt: now,
            },
            revision: {
              id: compiled.compiled.brief.revision_id,
              projectId: access.project.id,
              compiledHash: compiled.compiled.brief.compiled_hash,
              compilerVersion: compiled.compiled.brief.compiler_version,
              target: compiled.compiled.brief.target,
              briefMarkdown: compiled.compiled.markdown,
              briefJson: compiled.compiled.brief as unknown as Record<string, unknown>,
              tokenEstimate: compiled.compiled.brief.budget.used_estimate,
              sourceNodeIds: compiled.compiled.brief.sources.map((source) => source.node_id),
              sessionId,
              createdAt: now,
            },
            steal,
            now,
          });
          await writeActivity(deps.store, actor, {
            projectId: access.project.id,
            objectType: "session",
            objectId: started.session.id,
            verb: "start_work",
            payload: { task_id: task.id, steal },
            now,
          });
          if (started.stolenFrom) {
            await writeActivity(deps.store, actor, {
              projectId: access.project.id,
              objectType: "task",
              objectId: task.id,
              verb: "lock_stolen",
              payload: {
                from_session_id: started.stolenFrom,
                to_session_id: started.session.id,
              },
              now,
            });
          }
          return {
            session: presentSession(started.session),
            brief: compiled.compiled.brief,
          };
        },
      );
      return c.json(presented, 200);
    } catch (error) {
      if (error instanceof TaskLockedError) {
        return taskLocked(c, error.task);
      }
      if (error instanceof InvalidReferenceError) {
        return errorJson(c, 404, "not_found", `${error.entity} not found`);
      }
      throw error;
    }
  });

  app.post("/v1/sessions/:id/heartbeat", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    const session = await deps.store.findAgentSessionById(id);
    if (!session) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, session.projectId, "sessions:write");
    if (isResponse(access)) {
      return access;
    }
    if (session.status !== "active") {
      return errorJson(c, 409, "version_conflict", "session is not active", {
        reason: "session_inactive",
      });
    }
    const updated = await deps.store.heartbeatSession(session.id, deps.clock.now());
    if (!updated) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    return c.json(presentSession(updated));
  });

  app.post("/v1/sessions/:id/finish", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    const session = await deps.store.findAgentSessionById(id);
    if (!session) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, session.projectId, "sessions:write");
    if (isResponse(access)) {
      return access;
    }
    if (session.status !== "active") {
      return errorJson(c, 409, "version_conflict", "session is not active", {
        reason: "session_inactive",
      });
    }

    const body = await readObject(c);
    const summary = parseOptionalString(body?.["summary"], 8000);
    if (!summary || summary.length < HANDOFF_SUMMARY_MIN) {
      return errorJson(c, 400, "invalid_request", "summary must be at least 20 characters", {
        reason: "invalid_body",
      });
    }
    let nextSteps = "";
    if (body?.["next_steps"] !== undefined) {
      if (typeof body["next_steps"] !== "string" || body["next_steps"].length > 8000) {
        return errorJson(c, 400, "invalid_request", "invalid next_steps", { reason: "invalid_body" });
      }
      nextSteps = body["next_steps"];
    }
    let howToCheck = "";
    if (body?.["how_to_check"] !== undefined) {
      if (typeof body["how_to_check"] !== "string" || body["how_to_check"].length > 8000) {
        return errorJson(c, 400, "invalid_request", "invalid how_to_check", { reason: "invalid_body" });
      }
      howToCheck = body["how_to_check"];
    }
    const filesTouched = parseLinkedPaths(body?.["files_touched"]);
    if (!filesTouched.ok) {
      if (filesTouched.reason === "repo_ambiguous") {
        return errorJson(c, 400, "repo_ambiguous", "repo_id is required on files_touched");
      }
      return errorJson(c, 400, "invalid_request", "invalid files_touched", { reason: "invalid_body" });
    }
    const openQuestions = parseOpenQuestions(body?.["open_questions"]);
    if (openQuestions === undefined) {
      return errorJson(c, 400, "invalid_request", "invalid open_questions", {
        reason: "invalid_body",
      });
    }
    const statusRaw = body?.["status"] === undefined ? "done" : body["status"];
    if (typeof statusRaw !== "string" || !isFinishWorkStatus(statusRaw)) {
      return errorJson(c, 400, "invalid_request", "invalid status", { reason: "invalid_body" });
    }
    const taskStatus: FinishWorkStatus = statusRaw;

    const now = deps.clock.now();
    const actorIds = actorRef(access.actor);
    let finished;
    try {
      finished = await deps.store.finishWork({
        sessionId: session.id,
        handoffId: uuidv7(now.getTime()),
        summary,
        nextSteps: nextSteps ?? "",
        howToCheck,
        filesTouched: filesTouched.paths,
        openQuestions,
        taskStatus,
        actorType: actorIds.type,
        actorId: actorIds.id,
        now,
      });
    } catch (error) {
      if (error instanceof SessionNotActiveError) {
        return errorJson(c, 409, "version_conflict", "session is not active", {
          reason: "session_inactive",
        });
      }
      if (error instanceof TaskLockedError) {
        return taskLocked(c, error.task);
      }
      throw error;
    }
    if (!finished) {
      return errorJson(c, 404, "not_found", "session not found");
    }
    return c.json({
      session: presentSession(finished.session),
      handoff: presentHandoffResource(finished.handoff),
      task: finished.task ? presentTask(finished.task) : null,
    });
  });

  app.get("/v1/tasks/:id/handoff", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const taskId = c.req.param("id");
    if (!isUuid(taskId)) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    const task = await deps.store.findTaskById(taskId);
    if (!task || task.deletedAt) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, task.projectId, "tasks:read");
    if (isResponse(access)) {
      if (access.status === 404) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      return access;
    }
    const handoff = await deps.store.findLatestHandoffByTaskId(task.id);
    if (!handoff) {
      return errorJson(c, 404, "not_found", "handoff not found");
    }
    return c.json(presentHandoffResource(handoff));
  });
}

export function rejectAgentTerminalStatus(
  c: Context,
  actor: AuthActor,
  status: string,
): Response | undefined {
  if (actor.kind === "token" && !isAdminActor(actor) && isTerminalTaskStatus(status)) {
    return errorJson(c, 409, "finish_work_required", "use finish_work to enter a terminal status");
  }
  return undefined;
}
