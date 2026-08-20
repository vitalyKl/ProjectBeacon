import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono } from "hono";

import { actorActivityRef, requireProjectActor, type AccessDeps } from "../auth/access.js";
import type { RoadmapStore } from "../roadmap/store.js";
import type { ReportStore } from "./store.js";
import { errorJson, isMissingSchemaError } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { buildReportSnapshot, defaultReportTitle, reportMarkdown, reviewTitleFromBody } from "./build.js";
import { presentReport, presentReview } from "./present.js";

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

function schemaUnavailable(c: Parameters<typeof errorJson>[0]) {
  return errorJson(
    c,
    503,
    "integration_unavailable",
    "database schema is out of date; run pnpm --filter @beacon/db db:migrate",
    { reason: "missing_schema" },
  );
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

export type ReportDeps = AccessDeps & {
  store: ReportStore & RoadmapStore;
};

export function mountReports(app: Hono, deps: ReportDeps): void {
  app.get("/v1/projects/:id/reports", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "tasks:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    try {
      const records = await deps.store.listReports(access.project.id);
      const result = paginateRecords(records, page, (item) => item.createdAt);
      return c.json({
        items: result.items.map(presentReport),
        next_cursor: result.next_cursor,
      });
    } catch (error) {
      if (isMissingSchemaError(error)) {
        return schemaUnavailable(c);
      }
      throw error;
    }
  });

  app.post("/v1/projects/:id/reports", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "tasks:write");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const titleRaw = body?.["title"] === undefined ? undefined : parseOptionalString(body["title"], 200);
    if (body?.["title"] !== undefined && !titleRaw) {
      return errorJson(c, 400, "unauthorized", "invalid title", { reason: "invalid_body" });
    }
    const now = deps.clock.now();
    try {
      const [milestones, tasks, reviews] = await Promise.all([
        deps.store.listMilestones(access.project.id),
        deps.store.listTasks(access.project.id),
        deps.store.listReviews(access.project.id),
      ]);
      const snapshot = buildReportSnapshot({ now, milestones, tasks, reviews });
      const actor = actorActivityRef(access.actor);
      const report = await deps.store.createReport({
        id: uuidv7(now.getTime()),
        projectId: access.project.id,
        title: titleRaw ?? defaultReportTitle(now),
        bodyMd: reportMarkdown({
          projectName: access.project.name,
          snapshot,
          tasks,
          reviews,
        }),
        snapshot,
        createdByType: actor.type,
        createdById: actor.id,
        createdAt: now,
      });
      await deps.store.writeActivity({
        id: uuidv7(now.getTime() + 1),
        projectId: access.project.id,
        objectType: "report",
        objectId: report.id,
        actorType: actor.type,
        actorId: actor.id,
        verb: "create",
        payload: { title: report.title },
        createdAt: now,
      });
      return c.json(presentReport(report), 201);
    } catch (error) {
      if (isMissingSchemaError(error)) {
        return schemaUnavailable(c);
      }
      throw error;
    }
  });

  app.get("/v1/reports/:id", async (c) => {
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "report not found");
    }
    const report = await deps.store.findReportById(id);
    if (!report) {
      return errorJson(c, 404, "not_found", "report not found");
    }
    const access = await requireProjectActor(c, deps, report.projectId, "tasks:read");
    if (isResponse(access)) {
      return access;
    }
    return c.json(presentReport(report));
  });

  app.get("/v1/projects/:id/reviews", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "tasks:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    try {
      const records = await deps.store.listReviews(access.project.id);
      const result = paginateRecords(records, page, (item) => item.createdAt);
      return c.json({
        items: result.items.map(presentReview),
        next_cursor: result.next_cursor,
      });
    } catch (error) {
      if (isMissingSchemaError(error)) {
        return schemaUnavailable(c);
      }
      throw error;
    }
  });

  app.post("/v1/projects/:id/reviews", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:write");
    if (isResponse(access)) {
      return access;
    }
    const body = await readObject(c);
    const bodyMd = parseText(body?.["body_md"], 32_000);
    if (!bodyMd) {
      return errorJson(c, 400, "unauthorized", "body_md is required", { reason: "invalid_body" });
    }
    const title = reviewTitleFromBody(
      typeof body?.["title"] === "string" ? body["title"] : undefined,
      bodyMd,
      typeof body?.["source_path"] === "string" ? body["source_path"] : undefined,
    );
    const sourcePath =
      body?.["source_path"] === undefined ? null : parseOptionalString(body["source_path"], 1024);
    if (body?.["source_path"] !== undefined && sourcePath === undefined) {
      return errorJson(c, 400, "unauthorized", "invalid source_path", { reason: "invalid_body" });
    }
    const now = deps.clock.now();
    const actor = actorActivityRef(access.actor);
    try {
      const review = await deps.store.createReview({
        id: uuidv7(now.getTime()),
        projectId: access.project.id,
        title,
        bodyMd,
        source: "imported",
        sourcePath: sourcePath ?? null,
        status: "needs_review",
        createdByType: actor.type,
        createdById: actor.id,
        createdAt: now,
      });
      await deps.store.writeActivity({
        id: uuidv7(now.getTime() + 1),
        projectId: access.project.id,
        objectType: "review",
        objectId: review.id,
        actorType: actor.type,
        actorId: actor.id,
        verb: "import",
        payload: { title: review.title },
        createdAt: now,
      });
      return c.json(presentReview(review), 201);
    } catch (error) {
      if (isMissingSchemaError(error)) {
        return schemaUnavailable(c);
      }
      throw error;
    }
  });

  app.get("/v1/reviews/:id", async (c) => {
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "review not found");
    }
    const review = await deps.store.findReviewById(id);
    if (!review) {
      return errorJson(c, 404, "not_found", "review not found");
    }
    const access = await requireProjectActor(c, deps, review.projectId, "tasks:read");
    if (isResponse(access)) {
      return access;
    }
    return c.json(presentReview(review));
  });
}
