import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono, Context } from "hono";
import { authorizeProjectActor, isAdminActor, requireActor, requireProjectActor } from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { ProjectNotFoundError, UniqueViolationError } from "../auth/store.js";
import type { ProjectRepoRecord } from "../context/types.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentProjectRepo } from "./present.js";
import type { JobQueue } from "../jobs/queue.js";
import { parseLocalRootHint } from "./local-root.js";

const INDEX_MODES = ["sidecar", "bind_mount", "hosted_clone", "both"] as const;
const PROVIDERS = ["github", "local"] as const;

type IndexMode = (typeof INDEX_MODES)[number];
type Provider = (typeof PROVIDERS)[number];

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

function isIndexMode(value: string): value is IndexMode {
  return (INDEX_MODES as readonly string[]).includes(value);
}

function isProvider(value: string): value is Provider {
  return (PROVIDERS as readonly string[]).includes(value);
}

export function parseBindMountHint(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim().replaceAll("\\", "/");
  if (trimmed.length === 0 || trimmed.length > 512) {
    return undefined;
  }
  if (trimmed.startsWith("/") || /^[A-Za-z]:\//.test(trimmed)) {
    return undefined;
  }
  const parts = trimmed.split("/").filter((part) => part.length > 0 && part !== ".");
  if (parts.some((part) => part === ".." || part.includes("\0"))) {
    return undefined;
  }
  if (parts.length === 0) {
    return ".";
  }
  return parts.join("/");
}

export function mountRepos(app: Hono, deps: RepoDeps): void {
  app.get("/v1/projects/:id/repos", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "project:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listProjectRepos(access.project.id);
    const result = paginateRecords(records, page, (item) => item.lastIndexedAt ?? new Date(0));
    return c.json({
      items: result.items.map((repo) => presentProjectRepo(repo)),
      next_cursor: result.next_cursor,
    });
  });

  app.post("/v1/projects/:id/repos", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "admin");
    if (isResponse(access)) {
      return access;
    }

    const body = await readObject(c);
    const provider = parseProvider(body?.["provider"]);
    const indexMode = parseIndexMode(body?.["index_mode"] ?? "sidecar");
    const defaultBranch = parseOptionalString(body?.["default_branch"], 200) ?? "main";
    const remoteUrl =
      body?.["remote_url"] === undefined || body["remote_url"] === null
        ? null
        : parseOptionalString(body["remote_url"], 2048);
    const githubRepoId = parseOptionalBigInt(body?.["github_repo_id"]);
    const installationId = parseOptionalBigInt(body?.["installation_id"]);
    const localRootRaw = body?.["local_root_hint"];
    let localRootHint: string | null = null;
    if (localRootRaw !== undefined && localRootRaw !== null) {
      const parsed = parseLocalRootHint(localRootRaw);
      if (!parsed) {
        return errorJson(c, 400, "unauthorized", "invalid local_root_hint", {
          reason: "invalid_body",
        });
      }
      localRootHint = parsed;
    }

    if (
      !provider ||
      !indexMode ||
      remoteUrl === undefined ||
      githubRepoId === undefined ||
      installationId === undefined
    ) {
      return errorJson(c, 400, "unauthorized", "provider is required", { reason: "invalid_body" });
    }
    if (provider === "local" && !localRootHint) {
      return errorJson(c, 400, "unauthorized", "local_root_hint is required for local repos", {
        reason: "invalid_body",
      });
    }
    if (provider === "github" && !remoteUrl) {
      return errorJson(c, 400, "unauthorized", "remote_url is required for github repos", {
        reason: "invalid_body",
      });
    }

    const now = deps.clock.now();
    const repo: ProjectRepoRecord = {
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      provider,
      remoteUrl,
      defaultBranch,
      githubRepoId,
      installationId,
      localRootHint,
      indexMode,
      lastIndexedSha: null,
      lastIndexedAt: null,
    };

    let created: ProjectRepoRecord;
    try {
      created = await deps.store.createProjectRepo(repo);
    } catch (error) {
      if (error instanceof UniqueViolationError) {
        return errorJson(c, 409, "login_taken", "repo already exists", { reason: "unique" });
      }
      throw error;
    }
    if (provider === "github" && installationId !== null) {
      const existing = await deps.store.findGithubInstallationByInstallationId(installationId);
      await deps.store.upsertGithubInstallation({
        id: existing?.id ?? uuidv7(now.getTime()),
        orgId: access.project.orgId,
        installationId,
        accountLogin: existing?.accountLogin ?? "unknown",
        createdAt: existing?.createdAt ?? now,
      });
    }
    const project = await deps.store.setDefaultRepoIfEmpty(access.project.id, created.id, now);
    return c.json(presentProjectRepo({ ...created, projectId: project.id }), 201);
  });

  app.get("/v1/repos/:id", async (c) => {
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const repo = await deps.store.findProjectRepoById(id);
    if (!repo) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const access = await requireProjectActor(c, deps, repo.projectId, "project:read");
    if (isResponse(access)) {
      return access;
    }
    return c.json(presentProjectRepo(repo));
  });

  app.post("/v1/repos/:id/detect", async (c) => {
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const repo = await deps.store.findProjectRepoById(id);
    if (!repo) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const access = await requireProjectActor(c, deps, repo.projectId, "project:write");
    if (isResponse(access)) {
      return access;
    }

    const enqueue = async (): Promise<{ id: string; status: "accepted" }> => {
      const jobId = await deps.jobs.enqueueDetect(
        { repo_id: repo.id, project_id: repo.projectId },
        { singletonKey: `detect:${repo.id}` },
      );
      return { id: jobId, status: "accepted" };
    };

    const idempotencyKey = parseIdempotencyKey(c);
    if (!idempotencyKey) {
      const accepted = await enqueue();
      return c.json(accepted, 202);
    }

    const now = deps.clock.now();
    const actorId =
      access.actor.kind === "user"
        ? access.actor.user.id
        : access.actor.kind === "token"
          ? access.actor.token.id
          : "00000000-0000-0000-0000-000000000001";
    const actorType = access.actor.kind === "user" ? "user" : "token";
    const presented = await deps.store.withIdempotency(
      actorType,
      actorId,
      idempotencyKey,
      now,
      enqueue,
    );
    return c.json(presented, 202);
  });
}

function parseIdempotencyKey(c: Context): string | undefined {
  const header = c.req.header("idempotency-key")?.trim();
  if (!header || header.length > 256) {
    return undefined;
  }
  return header;
}

function parseProvider(value: unknown): Provider | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  return (PROVIDERS as readonly string[]).includes(value) ? (value as Provider) : undefined;
}

function parseIndexMode(value: unknown): IndexMode | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  return (INDEX_MODES as readonly string[]).includes(value) ? (value as IndexMode) : undefined;
}

function parseOptionalBigInt(value: unknown): bigint | null | undefined {
  if (value === undefined || value === null) {
    return null;
  }
  if (typeof value === "number" && Number.isInteger(value) && value >= 0) {
    return BigInt(value);
  }
  if (typeof value === "string" && /^[0-9]+$/.test(value)) {
    return BigInt(value);
  }
  return undefined;
}

export type RepoDeps = AuthDeps & {
  jobs: JobQueue;
};
