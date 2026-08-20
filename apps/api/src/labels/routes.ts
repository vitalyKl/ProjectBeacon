import { uniqueScopePaths } from "@beacon/context";
import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono } from "hono";

import { actorActivityRef, isAdminActor, requireActor, requireProjectActor, type AccessDeps } from "../auth/access.js";
import type { AuthActor } from "../auth/access.js";
import { UniqueViolationError } from "../auth/store.js";
import type { RepoStore } from "../repos/store.js";
import type { RoadmapStore } from "../roadmap/store.js";
import type { ContextStore } from "../context/store.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentTaskWithLabels } from "../roadmap/present.js";
import { parseSlug } from "../slug.js";
import { parseLabelIds } from "./parse.js";
import { presentLabel } from "./present.js";
import { isLabelStatus } from "./types.js";

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

function slugFromName(name: string): string | undefined {
  return parseSlug(name.replace(/[^a-zA-Z0-9]+/g, "-"));
}

function parseColor(value: unknown): string | null | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (value === null || value === "") {
    return null;
  }
  if (typeof value !== "string" || !/^#[0-9a-fA-F]{6}$/.test(value)) {
    return undefined;
  }
  return value.toLowerCase();
}

export type LabelDeps = AccessDeps & {
  store: ContextStore & RoadmapStore & RepoStore;
};

async function parseLabelPaths(
  store: Pick<RepoStore, "findProjectRepo">,
  projectId: string,
  value: unknown,
): Promise<{ ok: true; paths: { repo_id: string; path: string }[] } | { ok: false; reason: string }> {
  if (value === undefined) {
    return { ok: true, paths: [] };
  }
  if (!Array.isArray(value)) {
    return { ok: false, reason: "invalid_paths" };
  }
  const raw: { repo_id: string; path: string }[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return { ok: false, reason: "invalid_paths" };
    }
    const record = item as Record<string, unknown>;
    const path = parseOptionalString(record["path"], 1024);
    const repoId = record["repo_id"];
    if (!path || typeof repoId !== "string" || !isUuid(repoId)) {
      return { ok: false, reason: "invalid_paths" };
    }
    raw.push({ repo_id: repoId, path });
  }
  const paths = uniqueScopePaths(raw);
  for (const path of paths) {
    const repo = await store.findProjectRepo(path.repo_id);
    if (!repo || repo.projectId !== projectId) {
      return { ok: false, reason: "invalid_repo" };
    }
  }
  return { ok: true, paths };
}

function actorRef(actor: AuthActor): { type: string; id: string } {
  const ref = actorActivityRef(actor);
  if (actor.kind === "token") {
    return { type: "agent", id: actor.token.id };
  }
  return ref;
}

export function mountLabels(app: Hono, deps: LabelDeps): void {
  app.get("/v1/projects/:id/labels", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "tasks:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listLabels(access.project.id);
    const result = paginateRecords(records, page, (item) => item.createdAt);
    return c.json({
      items: result.items.map(presentLabel),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/labels", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "tasks:write");
    if (isResponse(access)) {
      return access;
    }

    const body = await readObject(c);
    const name = parseOptionalString(body?.["name"], 80);
    const slug = body?.["slug"] === undefined ? slugFromName(name ?? "") : parseSlug(body["slug"]);
    const description =
      body?.["description"] === undefined ? "" : parseOptionalString(body["description"], 400);
    const color = body?.["color"] === undefined ? null : parseColor(body["color"]);
    const requestedStatus =
      typeof body?.["status"] === "string" && isLabelStatus(body["status"])
        ? body["status"]
        : "active";
    const status =
      access.actor.kind === "token" && !isAdminActor(access.actor, access.role)
        ? "proposed"
        : requestedStatus;
    const paths = await parseLabelPaths(deps.store, access.project.id, body?.["paths"]);

    if (!name || !slug || description === undefined || color === undefined || !paths.ok) {
      return errorJson(c, 400, "unauthorized", "name is required", {
        reason: paths.ok ? "invalid_body" : paths.reason,
      });
    }

    const now = deps.clock.now();
    const actor = actorRef(access.actor);
    try {
      const created = await deps.store.createLabel({
        id: uuidv7(now.getTime()),
        projectId: access.project.id,
        slug,
        name,
        description: description ?? "",
        color,
        status,
        createdAt: now,
        paths: paths.paths,
      });
      await deps.store.writeActivity({
        id: uuidv7(now.getTime()),
        projectId: access.project.id,
        objectType: "label",
        objectId: created.id,
        actorType: actor.type,
        actorId: actor.id,
        verb: created.status === "proposed" ? "propose" : "create",
        payload: { slug: created.slug, status: created.status },
        createdAt: now,
      });
      return c.json(presentLabel(created), 201);
    } catch (error) {
      if (error instanceof UniqueViolationError) {
        return errorJson(c, 409, "login_taken", "label slug already exists", {
          reason: "slug_taken",
        });
      }
      throw error;
    }
  });

  app.patch("/v1/labels/:id", async (c) => {
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "label not found");
    }
    const existing = await deps.store.findLabelById(id);
    if (!existing) {
      return errorJson(c, 404, "not_found", "label not found");
    }
    const access = await requireProjectActor(c, deps, existing.projectId, "tasks:write");
    if (isResponse(access)) {
      return access;
    }
    const admin = isAdminActor(access.actor, access.role);
    if (access.actor.kind === "token" && !admin) {
      return errorJson(c, 403, "forbidden", "agents may only propose new labels");
    }

    const body = await readObject(c);
    if (!body) {
      return errorJson(c, 400, "unauthorized", "invalid body", { reason: "invalid_body" });
    }
    const name = body["name"] === undefined ? undefined : parseOptionalString(body["name"], 80);
    const slug = body["slug"] === undefined ? undefined : parseSlug(body["slug"]);
    const description =
      body["description"] === undefined ? undefined : parseOptionalString(body["description"], 400);
    const color = parseColor(body["color"]);
    const status =
      body["status"] === undefined
        ? undefined
        : typeof body["status"] === "string" && isLabelStatus(body["status"])
          ? body["status"]
          : undefined;
    const paths =
      body["paths"] === undefined
        ? undefined
        : await parseLabelPaths(deps.store, existing.projectId, body["paths"]);

    if (
      (body["name"] !== undefined && !name) ||
      (body["slug"] !== undefined && !slug) ||
      (body["description"] !== undefined && description === undefined) ||
      (body["color"] !== undefined && color === undefined) ||
      (body["status"] !== undefined && !status) ||
      (paths && !paths.ok)
    ) {
      return errorJson(c, 400, "unauthorized", "invalid label", {
        reason: paths && !paths.ok ? paths.reason : "invalid_body",
      });
    }

    try {
      const updated = await deps.store.updateLabel(existing.id, {
        ...(name ? { name } : {}),
        ...(slug ? { slug } : {}),
        ...(description !== undefined ? { description } : {}),
        ...(color !== undefined ? { color } : {}),
        ...(status ? { status } : {}),
        ...(paths && paths.ok ? { paths: paths.paths } : {}),
      });
      if (!updated) {
        return errorJson(c, 404, "not_found", "label not found");
      }
      const actor = actorRef(access.actor);
      await deps.store.writeActivity({
        id: uuidv7(deps.clock.now().getTime()),
        projectId: existing.projectId,
        objectType: "label",
        objectId: updated.id,
        actorType: actor.type,
        actorId: actor.id,
        verb: "update",
        payload: { slug: updated.slug, status: updated.status },
        createdAt: deps.clock.now(),
      });
      return c.json(presentLabel(updated));
    } catch (error) {
      if (error instanceof UniqueViolationError) {
        return errorJson(c, 409, "login_taken", "label slug already exists", {
          reason: "slug_taken",
        });
      }
      throw error;
    }
  });

  app.put("/v1/tasks/:id/labels", async (c) => {
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
    const access = await requireProjectActor(c, deps, task.projectId, "tasks:write");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const parsed = parseLabelIds(body?.["label_ids"]);
    if (!parsed.ok) {
      return errorJson(c, 400, "unauthorized", "label_ids is required", {
        reason: "invalid_body",
      });
    }
    const unique = parsed.ids;
    for (const labelId of unique) {
      const label = await deps.store.findLabelById(labelId);
      if (!label || label.projectId !== task.projectId) {
        return errorJson(c, 400, "unauthorized", "invalid label_ids", {
          reason: "invalid_label",
        });
      }
    }
    await deps.store.setTaskLabels(task.id, unique);
    return c.json(await presentTaskWithLabels(deps.store, task));
  });
}
