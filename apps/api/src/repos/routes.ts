import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono } from "hono";

import {
  authorizeProjectActor,
  isAdminActor,
  requireActor,
  requireProjectActor,
} from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { ProjectNotFoundError, UniqueViolationError } from "../auth/store.js";
import type { ProjectRepoRecord } from "../context/types.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentProjectRepo } from "./present.js";

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

export function mountRepos(app: Hono, deps: AuthDeps): void {
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
    if (!isAdminActor(access.actor, access.role)) {
      return errorJson(c, 403, "forbidden", "insufficient project role");
    }

    const body = await readObject(c);
    const providerRaw = typeof body?.["provider"] === "string" ? body["provider"] : "local";
    const indexModeRaw = typeof body?.["index_mode"] === "string" ? body["index_mode"] : undefined;
    if (!isProvider(providerRaw) || !indexModeRaw || !isIndexMode(indexModeRaw)) {
      return errorJson(c, 400, "unauthorized", "provider and index_mode are required", {
        reason: "invalid_body",
      });
    }

    const defaultBranch = parseOptionalString(body?.["default_branch"], 200) ?? "main";
    const remoteUrl =
      body?.["remote_url"] === undefined || body["remote_url"] === null
        ? null
        : parseOptionalString(body["remote_url"], 2048);
    if (
      body?.["remote_url"] !== undefined &&
      body["remote_url"] !== null &&
      remoteUrl === undefined
    ) {
      return errorJson(c, 400, "unauthorized", "invalid remote_url", { reason: "invalid_body" });
    }

    let localRootHint: string | null = null;
    if (indexModeRaw === "bind_mount") {
      const hint = parseBindMountHint(body?.["local_root_hint"]);
      if (!hint) {
        return errorJson(c, 400, "unauthorized", "local_root_hint must be a relative POSIX path", {
          reason: "invalid_local_root_hint",
        });
      }
      localRootHint = hint;
    } else if (body?.["local_root_hint"] !== undefined && body["local_root_hint"] !== null) {
      const hint = parseOptionalString(body["local_root_hint"], 512);
      if (!hint) {
        return errorJson(c, 400, "unauthorized", "invalid local_root_hint", {
          reason: "invalid_body",
        });
      }
      localRootHint = hint.replaceAll("\\", "/");
    }

    let githubRepoId: bigint | null = null;
    if (body?.["github_repo_id"] !== undefined && body["github_repo_id"] !== null) {
      const raw = body["github_repo_id"];
      if ((typeof raw !== "string" && typeof raw !== "number") || !/^[0-9]+$/.test(String(raw))) {
        return errorJson(c, 400, "unauthorized", "invalid github_repo_id", {
          reason: "invalid_body",
        });
      }
      githubRepoId = BigInt(raw);
    }

    let installationId: bigint | null = null;
    if (body?.["installation_id"] !== undefined && body["installation_id"] !== null) {
      const raw = body["installation_id"];
      if ((typeof raw !== "string" && typeof raw !== "number") || !/^[0-9]+$/.test(String(raw))) {
        return errorJson(c, 400, "unauthorized", "invalid installation_id", {
          reason: "invalid_body",
        });
      }
      installationId = BigInt(raw);
    }

    if (providerRaw === "github" && githubRepoId === null) {
      return errorJson(c, 400, "unauthorized", "github_repo_id is required", {
        reason: "invalid_body",
      });
    }

    const now = deps.clock.now();
    const record: ProjectRepoRecord = {
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      provider: providerRaw,
      remoteUrl: remoteUrl ?? null,
      defaultBranch,
      githubRepoId,
      installationId,
      localRootHint,
      indexMode: indexModeRaw,
      lastIndexedSha: null,
      lastIndexedAt: null,
    };

    try {
      const created = await deps.store.createProjectRepo(record);
      return c.json(presentProjectRepo(created), 201);
    } catch (error) {
      if (error instanceof ProjectNotFoundError) {
        return errorJson(c, 404, "not_found", "project not found");
      }
      if (error instanceof UniqueViolationError) {
        return errorJson(c, 409, "login_taken", "repo is already connected", {
          reason: "duplicate_repo",
        });
      }
      throw error;
    }
  });

  app.get("/v1/repos/:id", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const repo = await deps.store.findProjectRepoById(id);
    if (!repo) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const access = await authorizeProjectActor(c, deps, actor, repo.projectId, "project:read");
    if (isResponse(access)) {
      if (access.status === 404) {
        return errorJson(c, 404, "not_found", "repo not found");
      }
      return access;
    }
    return c.json(presentProjectRepo(repo));
  });
}
