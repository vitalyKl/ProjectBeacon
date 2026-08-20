import { isUuid } from "@beacon/shared";
import type { Context } from "hono";

import {
  authorizeProjectActor,
  requireProjectResource,
  type AccessDeps,
  type AuthActor,
} from "../auth/access.js";
import type { RepoStore } from "../repos/store.js";
import type { ProjectRepoRecord } from "../context/types.js";
import { errorJson } from "../errors.js";
import type { ProjectRecord } from "../orgs/types.js";

export type GithubResolveDeps = AccessDeps & {
  store: RepoStore;
};

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

export async function resolveGithubRepo(
  c: Context,
  deps: GithubResolveDeps,
  actor: AuthActor,
  repoId: string | undefined,
  projectId: string | undefined,
  needed: "project:read" | "integrations:write" | "tasks:write",
): Promise<{ repo: ProjectRepoRecord; project: ProjectRecord; actor: AuthActor } | Response> {
  let resolvedId = repoId;
  if (!resolvedId) {
    if (!projectId || !isUuid(projectId)) {
      return errorJson(c, 400, "repo_ambiguous", "repo_id is required");
    }
    const access = await authorizeProjectActor(c, deps, actor, projectId, needed);
    if (isResponse(access)) {
      return access;
    }
    if (!access.project.defaultRepoId) {
      return errorJson(c, 400, "repo_ambiguous", "repo_id is required");
    }
    resolvedId = access.project.defaultRepoId;
  }
  if (!isUuid(resolvedId)) {
    return errorJson(c, 404, "not_found", "repo not found");
  }
  const repo = await deps.store.findProjectRepoById(resolvedId);
  if (!repo) {
    return errorJson(c, 404, "not_found", "repo not found");
  }
  const access = await authorizeProjectActor(c, deps, actor, repo.projectId, needed);
  if (isResponse(access)) {
    if (access.status === 404) {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    return access;
  }
  return { repo, project: access.project, actor };
}

export async function requireGithubRepo(
  c: Context,
  deps: GithubResolveDeps,
  repoId: string,
  needed: "project:read" | "integrations:write" | "tasks:write",
): Promise<{ repo: ProjectRepoRecord; project: ProjectRecord; actor: AuthActor } | Response> {
  const loaded = await requireProjectResource(
    c,
    deps,
    async () => {
      if (!isUuid(repoId)) {
        return null;
      }
      return deps.store.findProjectRepoById(repoId);
    },
    needed,
    "repo not found",
  );
  if (isResponse(loaded)) {
    return loaded;
  }
  return { repo: loaded.resource, project: loaded.project, actor: loaded.actor };
}
