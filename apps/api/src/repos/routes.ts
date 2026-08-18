import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono, Context } from "hono";
import { authorizeProjectActor, isAdminActor, requireActor, requireProjectActor, actorHasCapability } from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { ProjectNotFoundError, UniqueViolationError } from "../auth/store.js";
import { enforceRateLimit } from "../auth/rate-limit.js";
import { CodeGatewayError, SIDECAR_SEEN_MS, createCodeGateway, taskChangedScopeQuery, type CodeGateway, type CodeQuery } from "../code/gateway.js";
import type { ProjectRepoRecord } from "../context/types.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { isHostedCloneEnabled } from "../flags.js";
import type { JobQueue } from "../jobs/queue.js";
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
  if (parts.length === 0) {
    return ".";
  return parts.join("/");
}

function hostedCloneRejected(c: Context, indexMode: IndexMode): Response | undefined {
  if (
    (indexMode === "hosted_clone" || indexMode === "both") &&
    !isHostedCloneEnabled(process.env)
  ) {
    return errorJson(c, 404, "not_found", "hosted clone is disabled", {
      reason: "flag_off",
    });
  if (!(INDEX_MODES as readonly string[]).includes(value)) {
  }
  const mode = value as IndexMode;
  if ((mode === "hosted_clone" || mode === "both") && !isHostedCloneEnabled(process.env)) {
    return undefined;
  }
  return mode;
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
  codeGateway?: CodeGateway;
};

async function loadAuthorizedRepo(
  c: Context,
  deps: RepoDeps,
  needed: "project:read" | "project:write" | "code:read",
) {
  const id = c.req.param("id");
  if (!id || !isUuid(id)) {
    return errorJson(c, 404, "not_found", "repo not found");
  }
  const repo = await deps.store.findProjectRepoById(id);
  if (!repo) {
    return errorJson(c, 404, "not_found", "repo not found");
  }
  const access = await requireProjectActor(c, deps, repo.projectId, needed);
  if (isResponse(access)) {
    if (access.status === 404) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    return access;
  }
  return { ...access, repo };
}

async function repoStatusExtras(deps: RepoDeps, repo: ProjectRepoRecord, gateway: CodeGateway) {
  const sidecar = await deps.store.findSidecarConnectionByRepoId(repo.id);
  const sidecarConnected = Boolean(
    sidecar && deps.clock.now().getTime() - sidecar.lastSeenAt.getTime() <= SIDECAR_SEEN_MS,
  );
  const workerIndexConnected =
    repo.indexMode === "bind_mount" ||
    repo.indexMode === "hosted_clone" ||
    repo.indexMode === "both"
      ? await gateway.health(repo)
      : false;
  return { sidecarConnected, workerIndexConnected };
}

function parsePositiveInt(
  value: string | undefined,
  fallback?: number,
): number | undefined | "invalid" {
  if (value === undefined || value === "") {
    return fallback;
  }
  if (!/^[0-9]+$/.test(value)) {
    return "invalid";
  }
  return Number(value);
}

function gatewayError(c: Context, error: unknown): Response {
  if (error instanceof CodeGatewayError) {
    return errorJson(
      c,
      error.status as 400 | 401 | 403 | 404 | 415 | 429 | 503,
      error.code as "unauthorized" | "not_found" | "unsupported_media" | "code_index_unavailable",
      error.messageText,
      error.details,
    );
  }
  throw error;
}

async function runCodeQuery(
  c: Context,
  deps: RepoDeps,
  repo: ProjectRepoRecord,
  actor: { kind: "user" | "token" | "worker"; id: string },
  query: CodeQuery,
  gateway: CodeGateway,
): Promise<Response> {
  const now = deps.clock.now();
  const limited = await enforceRateLimit(
    c,
    deps.store,
    actor.kind === "user" ? "user" : "token",
    actor.id,
    deps.rateLimits,
    now,
    "code",
  );
  if (limited) {
    return limited;
  }
  try {
    const result = await gateway.query(repo, query);
    if (query.kind === "file") {
      const bytes =
        result !== null && typeof result === "object" && "bytes" in result
          ? Number((result as { bytes?: unknown }).bytes ?? 0)
          : 0;
      const byteLimit = await enforceRateLimit(
        c,
        deps.store,
        actor.kind === "user" ? "user" : "token",
        actor.id,
        deps.rateLimits,
        now,
        "bytes",
        { bytes },
      );
      if (byteLimit) {
        return byteLimit;
      }
    }
    return c.json(result);
  } catch (error) {
    return gatewayError(c, error);
  }
}

function actorRateRef(actor: Awaited<ReturnType<typeof requireActor>>): {
  kind: "user" | "token";
  id: string;
} {
  if (actor instanceof Response) {
    return { kind: "token", id: "unknown" };
  }
  if (actor.kind === "user") {
    return { kind: "user", id: actor.user.id };
  }
  if (actor.kind === "token") {
    return { kind: "token", id: actor.token.id };
  }
  return { kind: "token", id: "00000000-0000-0000-0000-000000000001" };
}

export function mountRepos(app: Hono, deps: RepoDeps): void {
  const gateway =
    deps.codeGateway ??
    createCodeGateway({
      config: deps.config,
      store: deps.store,
      now: () => deps.clock.now(),
    });

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
    const requestedMode = body?.["index_mode"] ?? "sidecar";
    const indexMode = parseIndexMode(requestedMode);
    if (requestedMode !== undefined && indexMode === undefined) {
      return errorJson(c, 400, "unauthorized", "invalid index_mode", { reason: "invalid_body" });
    }
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
    if (
      (indexMode === "hosted_clone" || indexMode === "both") &&
      (provider !== "github" || !installationId)
    ) {
      return errorJson(c, 400, "unauthorized", "hosted clone requires a GitHub App installation", {
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
    const loaded = await loadAuthorizedRepo(c, deps, "project:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    return c.json(
      presentProjectRepo(loaded.repo, await repoStatusExtras(deps, loaded.repo, gateway)),
    );
  });

  app.patch("/v1/repos/:id", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "project:write");
    if (isResponse(loaded)) {
      return loaded;
    }
    const body = await readObject(c);
    if (!body || body["index_mode"] === undefined) {
      return errorJson(c, 400, "unauthorized", "index_mode is required", {
        reason: "invalid_body",
      });
    }
    const indexMode = parseIndexMode(body["index_mode"]);
    if (!indexMode) {
      return errorJson(c, 400, "unauthorized", "invalid index_mode", { reason: "invalid_body" });
    }
    if (
      (indexMode === "hosted_clone" || indexMode === "both") &&
      (loaded.repo.provider !== "github" || !loaded.repo.installationId)
    ) {
      return errorJson(c, 400, "unauthorized", "hosted clone requires a GitHub App installation", {
        reason: "invalid_body",
      });
    }
    const updated = await deps.store.updateProjectRepo(loaded.repo.id, { indexMode });
    const repo = updated ?? { ...loaded.repo, indexMode };
    if (
      (indexMode === "hosted_clone" || indexMode === "both") &&
      loaded.repo.indexMode !== indexMode
    ) {
      await deps.jobs.enqueueDetect(
        { repo_id: repo.id, project_id: repo.projectId },
        { singletonKey: `detect:${repo.id}` },
      );
    }
    return c.json(presentProjectRepo(repo, await repoStatusExtras(deps, repo, gateway)));
  });

  app.post("/v1/repos/:id/clone-invalidation/consume", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "project:write");
    if (isResponse(loaded)) {
      return loaded;
    }
    if (loaded.actor.kind !== "worker") {
      return errorJson(c, 403, "forbidden", "insufficient token scope");
    }
    const consumed = await deps.store.consumeCloneInvalidation(loaded.repo.id, deps.clock.now());
    return c.json({
      consumed: consumed
        ? { id: consumed.id, sha: consumed.sha, created_at: consumed.createdAt.toISOString() }
        : null,
    });
  });

  app.post("/v1/repos/:id/index", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "project:write");
    if (isResponse(loaded)) {
      return loaded;
    }
    if (loaded.actor.kind !== "worker") {
      return errorJson(c, 403, "forbidden", "insufficient token scope");
    }
    const body = await readObject(c);
    const sha =
      body?.["last_indexed_sha"] === undefined || body["last_indexed_sha"] === null
        ? body?.["last_indexed_sha"] === null
          ? null
          : undefined
        : parseOptionalString(body["last_indexed_sha"], 128);
    const atRaw = body?.["last_indexed_at"];
    let lastIndexedAt: Date | null | undefined;
    if (atRaw === undefined) {
      lastIndexedAt = deps.clock.now();
    } else if (atRaw === null) {
      lastIndexedAt = null;
    } else if (typeof atRaw === "string") {
      const parsed = new Date(atRaw);
      lastIndexedAt = Number.isNaN(parsed.getTime()) ? undefined : parsed;
    }
    if (
      sha === undefined &&
      body?.["last_indexed_sha"] !== undefined &&
      body["last_indexed_sha"] !== null
    ) {
      return errorJson(c, 400, "unauthorized", "invalid last_indexed_sha", {
        reason: "invalid_body",
      });
    }
    if (lastIndexedAt === undefined && atRaw !== undefined) {
      return errorJson(c, 400, "unauthorized", "invalid last_indexed_at", {
        reason: "invalid_body",
      });
    }
    const updated = await deps.store.updateProjectRepoIndex(loaded.repo.id, {
      lastIndexedSha: sha,
      lastIndexedAt,
    });
    return c.json(presentProjectRepo(updated ?? loaded.repo));
  });

  app.post("/v1/repos/:id/sidecar/register", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    if (loaded.actor.kind !== "token") {
      return errorJson(c, 401, "unauthorized", "sidecar register requires a project token");
    }
    const now = deps.clock.now();
    const row = await deps.store.upsertSidecarConnection({
      id: uuidv7(now.getTime()),
      repoId: loaded.repo.id,
      tokenId: loaded.actor.token.id,
      now,
    });
    return c.json({
      repo_id: row.repoId,
      last_seen_at: row.lastSeenAt.toISOString(),
      connected_at: row.connectedAt.toISOString(),
    });
  });

  app.get("/v1/repos/:id/tree", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const depth = parsePositiveInt(c.req.query("depth"), 2);
    if (depth === "invalid" || depth === undefined || depth < 1 || depth > 4) {
      return errorJson(c, 400, "unauthorized", "invalid depth", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      { kind: "tree", path: c.req.query("path"), depth },
      gateway,
    );
  });

  app.get("/v1/repos/:id/search", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const q = c.req.query("q")?.trim();
    if (!q) {
      return errorJson(c, 400, "unauthorized", "q is required", { reason: "invalid_query" });
    }
    const limit = parsePositiveInt(c.req.query("limit"), 50);
    if (limit === "invalid" || limit === undefined || limit < 1 || limit > 100) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      {
        kind: "search",
        q,
        mode: c.req.query("mode"),
        lang: c.req.query("lang"),
        pathPrefix: c.req.query("path_prefix"),
        limit,
      },
      gateway,
    );
  });

  app.get("/v1/repos/:id/files", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const path = c.req.query("path")?.trim();
    if (!path) {
      return errorJson(c, 400, "unauthorized", "path is required", { reason: "invalid_query" });
    }
    const startLine = parsePositiveInt(c.req.query("start_line"));
    const endLine = parsePositiveInt(c.req.query("end_line"));
    if (startLine === "invalid" || endLine === "invalid") {
      return errorJson(c, 400, "unauthorized", "invalid line range", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      { kind: "file", path, startLine, endLine },
      gateway,
    );
  });

  app.get("/v1/repos/:id/symbols", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const name = c.req.query("name")?.trim();
    if (!name) {
      return errorJson(c, 400, "unauthorized", "name is required", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      { kind: "symbol", name, path: c.req.query("path"), symbolKind: c.req.query("kind") },
      gateway,
    );
  });

  app.get("/v1/repos/:id/owners", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const path = c.req.query("path")?.trim();
    if (!path) {
      return errorJson(c, 400, "unauthorized", "path is required", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      { kind: "owners", path },
      gateway,
    );
  });

  app.get("/v1/repos/:id/related", async (c) => {
    const loaded = await loadAuthorizedRepo(c, deps, "code:read");
    if (isResponse(loaded)) {
      return loaded;
    }
    const path = c.req.query("path")?.trim();
    if (!path) {
      return errorJson(c, 400, "unauthorized", "path is required", { reason: "invalid_query" });
    }
    const limit = parsePositiveInt(c.req.query("limit"), 50);
    if (limit === "invalid" || limit === undefined || limit < 1 || limit > 100) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      loaded.repo,
      actorRateRef(loaded.actor),
      { kind: "related", path, limit },
      gateway,
    );
  });

  app.get("/v1/tasks/:id/changed-scope", async (c) => {
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
    const access = await authorizeProjectActor(c, deps, actor, task.projectId, "code:read");
    if (isResponse(access)) {
      if (access.status === 404) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      return access;
    }
    if (!actorHasCapability(access.actor, "tasks:read", access.role)) {
      return errorJson(c, 403, "forbidden", "insufficient token scope");
    }
    const repos = await deps.store.listProjectRepos(access.project.id);
    const requested = c.req.query("repo_id")?.trim();
    let repo: ProjectRepoRecord | undefined;
    if (requested) {
      repo = repos.find((item) => item.id === requested);
      if (!repo) {
        return errorJson(c, 404, "not_found", "repo not found");
      }
    } else {
      repo =
        (access.project.defaultRepoId
          ? repos.find((item) => item.id === access.project.defaultRepoId)
          : undefined) ?? (repos.length === 1 ? repos[0] : undefined);
      if (!repo) {
        return errorJson(c, 400, "repo_ambiguous", "repo_id is required");
      }
    }
    const limit = parsePositiveInt(c.req.query("limit"), 50);
    if (limit === "invalid" || limit === undefined || limit < 1 || limit > 100) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_query" });
    }
    return runCodeQuery(
      c,
      deps,
      repo,
      actorRateRef(access.actor),
      taskChangedScopeQuery(task, repo.id, limit),
      gateway,
    );
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
