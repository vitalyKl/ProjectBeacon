import { isUuid, uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import {
  actorIdempotencyRef,
  authorizeProjectActor,
  requireActor,
  requireProjectActor,
  taskStatusOnCreate,
} from "../auth/access.js";
import { isGithubAppConfigured, type AuthConfig } from "../auth/config.js";
import type { AuthDeps } from "../auth/routes.js";
import { UniqueViolationError, VersionConflictError } from "../auth/store.js";
import type { ProjectRepoRecord } from "../context/types.js";
import { errorJson } from "../errors.js";
import { parseOptionalString, readObject } from "../http.js";
import type { JobQueue } from "../jobs/queue.js";
import { presentTask } from "../roadmap/present.js";
import { paginateRecords, parsePageQuery } from "../roadmap/page.js";
import type { TaskRecord } from "../roadmap/types.js";
import {
  createInstallationToken,
  decodeGithubPageCursor,
  encodeGithubPageCursor,
  githubApiRequest,
  parseGithubRemote,
  presentGithubIssue,
  presentGithubPull,
} from "./app.js";
import { presentGithubLink, presentGithubSyncState } from "./present.js";
import { resolveGithubRepo } from "./resolve.js";
import { githubIssuesMode } from "./settings.js";
import { verifyGithubWebhookSignature } from "./signature.js";

export type GithubDeps = AuthDeps & {
  jobs: JobQueue;
};

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

function parsePositiveInt(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isInteger(value) && value > 0) {
    return value;
  }
  if (typeof value === "string" && /^[0-9]+$/.test(value)) {
    const parsed = Number(value);
    if (Number.isInteger(parsed) && parsed > 0) {
      return parsed;
    }
  }
  return undefined;
}

function parsePositiveBigInt(value: unknown): bigint | undefined {
  if (typeof value === "bigint" && value > 0n) {
    return value;
  }
  if (typeof value === "number" && Number.isInteger(value) && value > 0) {
    return BigInt(value);
  }
  if (typeof value === "string" && /^\d+$/.test(value)) {
    try {
      const parsed = BigInt(value);
      return parsed > 0n ? parsed : undefined;
    } catch {
      return undefined;
    }
  }
  return undefined;
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  return value as Record<string, unknown>;
}

function githubAppReady(config: AuthConfig): boolean {
  return isGithubAppConfigured(config);
}

async function requireInstalledGithubRepo(
  c: Context,
  deps: GithubDeps,
  repo: ProjectRepoRecord,
): Promise<Response | undefined> {
  if (repo.provider !== "github" || repo.installationId === null || repo.githubRepoId === null) {
    return errorJson(c, 503, "integration_unavailable", "github app is not connected", {
      reason: "repo_not_installed",
    });
  }
  const installation = await deps.store.findGithubInstallationByInstallationId(repo.installationId);
  if (!installation) {
    return errorJson(c, 503, "integration_unavailable", "github app is not connected", {
      reason: "installation_missing",
    });
  }
  return undefined;
}

async function installationTokenFor(
  deps: GithubDeps,
  installationId: bigint,
): Promise<string | undefined> {
  const appId = deps.config.githubAppId;
  const privateKey = deps.config.githubAppPrivateKey;
  if (!appId || !privateKey) {
    return undefined;
  }
  return createInstallationToken(
    installationId,
    appId,
    privateKey,
    deps.githubFetch,
    deps.clock.now().getTime(),
  );
}

function issueQuery(state: string | undefined, q: string | undefined): string {
  const params = new URLSearchParams({ per_page: "100" });
  params.set("state", state === "closed" || state === "all" ? state : "open");
  if (q) {
    params.set("q", q);
  }
  return params.toString();
}

function prQuery(state: string | undefined): string {
  const params = new URLSearchParams({ per_page: "100" });
  params.set("state", state === "closed" || state === "all" ? state : "open");
  return params.toString();
}

function githubListPath(
  remote: { owner: string; repo: string },
  kind: "issues" | "pulls",
  query: string,
  cursor: string | undefined,
): string | undefined {
  if (cursor) {
    return decodeGithubPageCursor(cursor);
  }
  return `/repos/${remote.owner}/${remote.repo}/${kind}?${query}`;
}

async function persistGithubInstallation(
  deps: GithubDeps,
  installationId: bigint,
  accountLogin: string,
  orgId: string,
  now: Date,
): Promise<void> {
  const existing = await deps.store.findGithubInstallationByInstallationId(installationId);
  await deps.store.upsertGithubInstallation({
    id: existing?.id ?? uuidv7(now.getTime()),
    orgId: existing?.orgId ?? orgId,
    installationId,
    accountLogin: accountLogin || existing?.accountLogin || "unknown",
    createdAt: existing?.createdAt ?? now,
  });
}

async function persistInstallationFromWebhook(
  deps: GithubDeps,
  payload: Record<string, unknown>,
  installationId: bigint,
  now: Date,
): Promise<void> {
  const installation = asRecord(payload["installation"]);
  const account = asRecord(installation?.["account"]);
  const accountLogin =
    typeof account?.["login"] === "string" && account["login"].trim().length > 0
      ? account["login"].trim()
      : "unknown";
  const existing = await deps.store.findGithubInstallationByInstallationId(installationId);
  if (existing) {
    await persistGithubInstallation(deps, installationId, accountLogin, existing.orgId, now);
    return;
  }
  const repoIds: bigint[] = [];
  const repository = asRecord(payload["repository"]);
  const repositoryId = parsePositiveBigInt(repository?.["id"]);
  if (repositoryId) {
    repoIds.push(repositoryId);
  }
  for (const key of ["repositories", "repositories_added"] as const) {
    const rows = payload[key];
    if (!Array.isArray(rows)) {
      continue;
    }
    for (const row of rows) {
      const id = parsePositiveBigInt(asRecord(row)?.["id"]);
      if (id) {
        repoIds.push(id);
      }
    }
  }
  for (const githubRepoId of repoIds) {
    const repos = await deps.store.listProjectReposByGithubRepoId(githubRepoId);
    for (const repo of repos) {
      const project = await deps.store.findProjectById(repo.projectId);
      if (project) {
        await persistGithubInstallation(deps, installationId, accountLogin, project.orgId, now);
        return;
      }
    }
  }
}

function mentionsIssue(text: string, issueNumber: number): boolean {
  return new RegExp(`(?:^|\\W)#${issueNumber}(?:\\W|$)`).test(text);
}

async function issueNumberForGithubId(
  token: string,
  remote: { owner: string; repo: string },
  githubIssueId: bigint,
  githubFetch: typeof fetch,
): Promise<number | undefined> {
  let path: string | undefined = `/repos/${remote.owner}/${remote.repo}/issues?${issueQuery(
    "all",
    undefined,
  )}`;
  for (let page = 0; page < 100 && path; page += 1) {
    const result = await githubApiRequest(token, path, githubFetch);
    if (!result.ok || !Array.isArray(result.body)) {
      return undefined;
    }
    for (const item of result.body) {
      const issue = presentGithubIssue(item);
      if (issue && issue.id === githubIssueId.toString()) {
        return issue.number;
      }
    }
    path = result.nextUrl;
  }
  return undefined;
}

async function enqueueAccepted(
  c: Context,
  deps: GithubDeps,
  actorType: "token" | "user",
  actorId: string,
  produce: () => Promise<{ id: string; status: "accepted" }>,
): Promise<Response> {
  const idempotencyKey = parseIdempotencyKey(c);
  if (!idempotencyKey) {
    return c.json(await produce(), 202);
  }
  const presented = await deps.store.withIdempotency(
    actorType,
    actorId,
    idempotencyKey,
    deps.clock.now(),
    produce,
  );
  return c.json(presented, 202);
}

export function mountGithub(app: Hono, deps: GithubDeps): void {
  app.post("/v1/webhooks/github", async (c) => {
    const secret = deps.config.githubAppWebhookSecret;
    if (!secret) {
      return errorJson(c, 503, "integration_unavailable", "github app is not configured");
    }
    const rawBody = await c.req.text();
    if (!verifyGithubWebhookSignature(rawBody, c.req.header("x-hub-signature-256"), secret)) {
      return errorJson(c, 401, "unauthorized", "invalid webhook signature");
    }

    let parsed: unknown;
    try {
      parsed = rawBody.length > 0 ? (JSON.parse(rawBody) as unknown) : {};
    } catch {
      return errorJson(c, 400, "unauthorized", "invalid webhook payload", {
        reason: "invalid_body",
      });
    }
    const payload = asRecord(parsed) ?? {};
    const event = c.req.header("x-github-event")?.trim() ?? "";
    const installation = asRecord(payload["installation"]);
    const installationId = parsePositiveBigInt(installation?.["id"]);
    if (installationId) {
      await persistInstallationFromWebhook(deps, payload, installationId, deps.clock.now());
    }

    if (event === "installation" || event === "installation_repositories") {
      return c.json({ received: true }, 202);
    }

    const repository = asRecord(payload["repository"]);
    const githubRepoId = parsePositiveBigInt(repository?.["id"]);
    if (!githubRepoId) {
      return c.json({ received: true }, 202);
    }
    const repos = (await deps.store.listProjectReposByGithubRepoId(githubRepoId)).filter((repo) => {
      if (repo.provider !== "github") {
        return false;
      }
      return !(
        repo.installationId !== null &&
        installationId &&
        repo.installationId !== installationId
      );
    });
    if (repos.length === 0) {
      return c.json({ received: true }, 202);
    }

    if (event === "push") {
      for (const repo of repos) {
        await deps.jobs.enqueueGithubInvalidate(
          {
            repo_id: repo.id,
            project_id: repo.projectId,
            ref: typeof payload["ref"] === "string" ? payload["ref"] : undefined,
            before: typeof payload["before"] === "string" ? payload["before"] : undefined,
            after: typeof payload["after"] === "string" ? payload["after"] : undefined,
          },
          { singletonKey: `github-invalidate:${repo.id}` },
        );
      }
      return c.json({ received: true }, 202);
    }

    if (event === "issues") {
      const issue = asRecord(payload["issue"]);
      const issueNumber = parsePositiveInt(issue?.["number"]);
      for (const repo of repos) {
        const project = await deps.store.findProjectById(repo.projectId);
        if (!project || githubIssuesMode(project.settings) !== "import") {
          continue;
        }
        await deps.jobs.enqueueGithubImport(
          {
            repo_id: repo.id,
            project_id: repo.projectId,
            issue_number: issueNumber,
          },
          {
            singletonKey: issueNumber
              ? `github-import:${repo.id}:${issueNumber}`
              : `github-import:${repo.id}`,
          },
        );
      }
      return c.json({ received: true }, 202);
    }

    if (event === "pull_request") {
      return c.json({ received: true }, 202);
    }

    return c.json({ received: true }, 202);
  });

  app.get("/v1/repos/:id/github/issues", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "project:read",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    if (!githubAppReady(deps.config)) {
      return errorJson(c, 503, "integration_unavailable", "github app is not configured");
    }
    const installed = await requireInstalledGithubRepo(c, deps, resolved.repo);
    if (installed) {
      return installed;
    }
    const remote = parseGithubRemote(resolved.repo.remoteUrl);
    const token = resolved.repo.installationId
      ? await installationTokenFor(deps, resolved.repo.installationId)
      : undefined;
    if (!remote || !token) {
      return errorJson(c, 503, "integration_unavailable", "github app is not connected");
    }
    const state = c.req.query("state");
    const q = parseOptionalString(c.req.query("q"), 200);
    const path = githubListPath(remote, "issues", issueQuery(state, q), c.req.query("cursor"));
    if (!path) {
      return errorJson(c, 400, "unauthorized", "invalid cursor", { reason: "invalid_cursor" });
    }
    const result = await githubApiRequest(token, path, deps.githubFetch);
    if (!result.ok || !Array.isArray(result.body)) {
      return errorJson(c, 503, "integration_unavailable", "github request failed");
    }
    const items = result.body
      .map((item) => presentGithubIssue(item))
      .filter((item): item is NonNullable<typeof item> => Boolean(item));
    const filtered = q
      ? items.filter((item) => item.title.toLowerCase().includes(q.toLowerCase()))
      : items;
    return c.json({
      items: filtered,
      next_cursor: result.nextUrl ? encodeGithubPageCursor(result.nextUrl) : null,
    });
  });

  app.get("/v1/repos/:id/github/pulls", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "project:read",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    if (!githubAppReady(deps.config)) {
      return errorJson(c, 503, "integration_unavailable", "github app is not configured");
    }
    const installed = await requireInstalledGithubRepo(c, deps, resolved.repo);
    if (installed) {
      return installed;
    }
    const remote = parseGithubRemote(resolved.repo.remoteUrl);
    const token = resolved.repo.installationId
      ? await installationTokenFor(deps, resolved.repo.installationId)
      : undefined;
    if (!remote || !token) {
      return errorJson(c, 503, "integration_unavailable", "github app is not connected");
    }
    const taskIdRaw = c.req.query("task_id");
    let linkedIssueNumber: number | undefined;
    if (taskIdRaw !== undefined) {
      if (!isUuid(taskIdRaw)) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      const task = await deps.store.findTaskById(taskIdRaw);
      if (!task || task.deletedAt || task.projectId !== resolved.project.id) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      if (task.githubIssueId === null) {
        return c.json({ items: [], next_cursor: null });
      }
      linkedIssueNumber = await issueNumberForGithubId(
        token,
        remote,
        task.githubIssueId,
        deps.githubFetch,
      );
      if (!linkedIssueNumber) {
        return c.json({ items: [], next_cursor: null });
      }
    }
    const path = githubListPath(
      remote,
      "pulls",
      prQuery(c.req.query("state")),
      c.req.query("cursor"),
    );
    if (!path) {
      return errorJson(c, 400, "unauthorized", "invalid cursor", { reason: "invalid_cursor" });
    }
    const result = await githubApiRequest(token, path, deps.githubFetch);
    if (!result.ok || !Array.isArray(result.body)) {
      return errorJson(c, 503, "integration_unavailable", "github request failed");
    }
    const items = result.body
      .map((item) => presentGithubPull(item))
      .filter((item): item is NonNullable<typeof item> => Boolean(item))
      .filter((item) => {
        if (linkedIssueNumber === undefined) {
          return true;
        }
        return mentionsIssue(`${item.title}\n${item.body}`, linkedIssueNumber);
      });
    return c.json({
      items,
      next_cursor: result.nextUrl ? encodeGithubPageCursor(result.nextUrl) : null,
    });
  });

  app.post("/v1/repos/:id/github/sync", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "integrations:write",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    if (!githubAppReady(deps.config)) {
      return errorJson(c, 503, "integration_unavailable", "github app is not configured");
    }
    const installed = await requireInstalledGithubRepo(c, deps, resolved.repo);
    if (installed) {
      return installed;
    }
    if (githubIssuesMode(resolved.project.settings) !== "import") {
      return errorJson(c, 400, "unauthorized", "github issue import is off", {
        reason: "github_issues_off",
      });
    }
    const idempotency = actorIdempotencyRef(resolved.actor);
    return enqueueAccepted(c, deps, idempotency.type, idempotency.id, async () => {
      const jobId = await deps.jobs.enqueueGithubImport(
        { repo_id: resolved.repo.id, project_id: resolved.project.id },
        { singletonKey: `github-import:${resolved.repo.id}` },
      );
      return { id: jobId, status: "accepted" };
    });
  });

  app.post("/v1/tasks/:id/github-issue", async (c) => {
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
    const access = await authorizeProjectActor(c, deps, actor, task.projectId, "tasks:write");
    if (isResponse(access)) {
      if (access.status === 404) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      return access;
    }
    const body = await readObject(c);
    const issueNumber = parsePositiveInt(body?.["issue_number"]);
    const repoIdRaw = body?.["repo_id"];
    const repoId =
      repoIdRaw === undefined ? undefined : typeof repoIdRaw === "string" ? repoIdRaw : "";
    if (!issueNumber) {
      return errorJson(c, 400, "unauthorized", "issue_number is required", {
        reason: "invalid_body",
      });
    }
    const resolved = await resolveGithubRepo(c, deps, actor, repoId, task.projectId, "tasks:write");
    if (isResponse(resolved)) {
      return resolved;
    }
    if (resolved.repo.projectId !== task.projectId) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    if (!githubAppReady(deps.config)) {
      return errorJson(c, 503, "integration_unavailable", "github app is not configured");
    }
    const installed = await requireInstalledGithubRepo(c, deps, resolved.repo);
    if (installed) {
      return installed;
    }
    const remote = parseGithubRemote(resolved.repo.remoteUrl);
    const token = resolved.repo.installationId
      ? await installationTokenFor(deps, resolved.repo.installationId)
      : undefined;
    if (!remote || !token) {
      return errorJson(c, 503, "integration_unavailable", "github app is not connected");
    }
    const result = await githubApiRequest(
      token,
      `/repos/${remote.owner}/${remote.repo}/issues/${issueNumber}`,
      deps.githubFetch,
    );
    const issue = presentGithubIssue(result.body);
    if (!result.ok || !issue) {
      return errorJson(c, 404, "not_found", "github issue not found");
    }
    const githubIssueId = BigInt(issue.id);
    const existing = await deps.store.findTaskByGithubIssueId(task.projectId, githubIssueId);
    if (existing && existing.id !== task.id) {
      return errorJson(c, 409, "login_taken", "github issue is already linked", {
        reason: "unique",
      });
    }
    let updated: { task: TaskRecord } | undefined;
    try {
      updated = await deps.store.updateTask(
        task.id,
        task.version,
        { githubIssueId },
        deps.clock.now(),
      );
    } catch (error) {
      if (error instanceof UniqueViolationError) {
        return errorJson(c, 409, "login_taken", "github issue is already linked", {
          reason: "unique",
        });
      }
      throw error;
    }
    if (!updated) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    return c.json({
      ...presentGithubLink(updated.task),
      issue_number: issue.number,
      html_url: issue.html_url,
    });
  });

  app.post("/v1/repos/:id/github/imported-issues", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "tasks:write",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    if (resolved.actor.kind !== "worker") {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    if (githubIssuesMode(resolved.project.settings) !== "import") {
      return errorJson(c, 400, "unauthorized", "github issue import is off", {
        reason: "github_issues_off",
      });
    }
    const repo = resolved.repo;
    const body = await readObject(c);
    const issuesRaw = body?.["issues"];
    if (!Array.isArray(issuesRaw)) {
      return errorJson(c, 400, "unauthorized", "issues is required", { reason: "invalid_body" });
    }

    const now = deps.clock.now();
    const items: ReturnType<typeof presentTask>[] = [];
    let createdOffset = 0;
    for (const raw of issuesRaw) {
      const issue = asRecord(raw);
      const githubIssueId = parsePositiveBigInt(issue?.["github_issue_id"] ?? issue?.["id"]);
      const number = parsePositiveInt(issue?.["number"]);
      const title = parseOptionalString(issue?.["title"], 200);
      const description =
        typeof issue?.["body"] === "string"
          ? issue["body"].slice(0, 8000)
          : typeof issue?.["description"] === "string"
            ? issue["description"].slice(0, 8000)
            : "";
      if (!githubIssueId || !number || !title) {
        return errorJson(c, 400, "unauthorized", "invalid imported issue", {
          reason: "invalid_body",
        });
      }
      const existing = await deps.store.findTaskByGithubIssueId(resolved.project.id, githubIssueId);
      if (existing) {
        try {
          const updated = await deps.store.updateTask(
            existing.id,
            existing.version,
            { title, description },
            now,
          );
          items.push(presentTask(updated?.task ?? existing));
        } catch (error) {
          if (!(error instanceof VersionConflictError)) {
            throw error;
          }
          items.push(presentTask(error.current));
        }
        continue;
      }
      try {
        const created = await deps.store.withIdempotency(
          "token",
          actorIdempotencyRef(resolved.actor).id,
          `github-import:${repo.id}:${githubIssueId.toString()}`,
          now,
          async (writes) => {
            const task = await writes.createTask({
              id: uuidv7(now.getTime() + createdOffset),
              projectId: resolved.project.id,
              milestoneId: null,
              parentId: null,
              title,
              description,
              status: taskStatusOnCreate(resolved.actor, "backlog") as TaskRecord["status"],
              priority: 0,
              type: "task",
              version: 1,
              assigneeUserId: null,
              assigneeAgentName: null,
              agentBrief: "",
              howToCheck: "",
              linkedPaths: [],
              githubIssueId,
              lockedBySessionId: null,
              lockExpiresAt: null,
              deletedAt: null,
              createdAt: now,
              updatedAt: now,
            });
            return presentTask(task);
          },
        );
        createdOffset += 1;
        items.push(created as ReturnType<typeof presentTask>);
      } catch (error) {
        if (!(error instanceof UniqueViolationError)) {
          throw error;
        }
        const raced = await deps.store.findTaskByGithubIssueId(resolved.project.id, githubIssueId);
        if (!raced) {
          throw error;
        }
        items.push(presentTask(raced));
      }
    }

    const cursor =
      typeof body?.["cursor"] === "string" && body["cursor"].length > 0 ? body["cursor"] : null;
    await deps.store.upsertGithubSyncState({
      repoId: repo.id,
      lastCursor: cursor,
      lastSyncedAt: now,
    });
    return c.json({ items, count: items.length });
  });

  app.post("/v1/repos/:id/github/invalidations", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "integrations:write",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    if (resolved.actor.kind !== "worker") {
      return errorJson(c, 404, "not_found", "repo not found");
    }
    const body = await readObject(c);
    const now = deps.clock.now();
    await deps.store.writeActivity({
      id: uuidv7(now.getTime()),
      projectId: resolved.project.id,
      objectType: "repo",
      objectId: resolved.repo.id,
      actorType: "system",
      actorId: actorIdempotencyRef(resolved.actor).id,
      verb: "github_clone_invalidated",
      payload: {
        ref: typeof body?.["ref"] === "string" ? body["ref"] : null,
        before: typeof body?.["before"] === "string" ? body["before"] : null,
        after: typeof body?.["after"] === "string" ? body["after"] : null,
      },
      createdAt: now,
    });
    return c.json({ recorded: true }, 202);
  });

  app.get("/v1/repos/:id/github/sync-state", async (c) => {
    const actor = await requireActor(c, deps);
    if (isResponse(actor)) {
      return actor;
    }
    const resolved = await resolveGithubRepo(
      c,
      deps,
      actor,
      c.req.param("id"),
      undefined,
      "project:read",
    );
    if (isResponse(resolved)) {
      return resolved;
    }
    const state = await deps.store.findGithubSyncState(resolved.repo.id);
    return c.json(
      state
        ? presentGithubSyncState(state)
        : { repo_id: resolved.repo.id, last_cursor: null, last_synced_at: null },
    );
  });

  app.get("/v1/projects/:id/github/issues", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "project:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const linked = (await deps.store.listTasks(access.project.id)).filter(
      (task) => task.githubIssueId !== null,
    );
    const result = paginateRecords(linked, page, (item) => item.updatedAt);
    return c.json({
      items: result.items.map((task) => presentTask(task)),
      next_cursor: result.next_cursor,
    });
  });
}
