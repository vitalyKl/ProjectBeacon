import { createHmac, generateKeyPairSync } from "node:crypto";

import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { MemoryJobQueue } from "./jobs/queue.js";
const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";
const WORKER_TOKEN = "deploy-time-worker-token";
const WEBHOOK_SECRET = "webhook-secret";
const { privateKey: APP_PRIVATE_KEY } = generateKeyPairSync("rsa", { modulusLength: 2048 });

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    workerToken: WORKER_TOKEN,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    githubAppId: "12345",
    githubAppPrivateKey: APP_PRIVATE_KEY.export({ type: "pkcs8", format: "pem" }).toString(),
    githubAppWebhookSecret: WEBHOOK_SECRET,
    secureCookies: false,
    trustProxy: false,
    indexRpcUrl: "http://127.0.0.1:7744",
    indexRpcToken: "index-rpc-test",
    ...overrides,
  };
}

function sessionCookie(res: Response): string | undefined {
  const header = res.headers.get("set-cookie");
  if (!header) {
    return undefined;
  }
  const match = /(?:^|,\s*)beacon_session=([^;]+)/.exec(header);
  return match?.[1];
}

function cookieHeader(token: string): string {
  return `beacon_session=${token}`;
}

function signWebhook(body: string): string {
  return `sha256=${createHmac("sha256", WEBHOOK_SECRET).update(body).digest("hex")}`;
}

async function registerUser(store: MemoryAuthStore, login: string, jobs = new MemoryJobQueue()) {
  const app = createApp({ store, config: testConfig(), checkReady: async () => true, jobs });
  const res = await app.request("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ login, password: STRONG_PASSWORD }),
  });
  const token = sessionCookie(res);
  const body = (await res.json()) as { id: string; login: string };
  return { app, token: token!, user: body, jobs };
}

async function createProject(app: ReturnType<typeof createApp>, token: string, slug: string) {
  const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token) } });
  const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
  const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
    method: "POST",
    headers: { cookie: cookieHeader(token), "content-type": "application/json" },
    body: JSON.stringify({ slug, name: slug }),
  });
  return (await created.json()) as { id: string };
}

async function createGithubRepo(
  app: ReturnType<typeof createApp>,
  token: string,
  projectId: string,
  options: { issues?: "import" | "off"; githubRepoId?: number; installationId?: number } = {},
) {
  const created = await app.request(`/v1/projects/${projectId}/repos`, {
    method: "POST",
    headers: { cookie: cookieHeader(token), "content-type": "application/json" },
    body: JSON.stringify({
      provider: "github",
      remote_url: "https://github.com/acme/demo",
      github_repo_id: options.githubRepoId ?? 4242,
      installation_id: options.installationId ?? 77,
    }),
  });
  const repo = (await created.json()) as { id: string };
  if (options.issues === "import") {
    const patched = await app.request(`/v1/projects/${projectId}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ settings: { github: { issues: "import" } } }),
    });
    if (patched.status !== 200) {
      throw new Error(`failed to enable github import: ${patched.status}`);
    }
  }
  return repo;
}

function githubFetchImpl(
  issues: Array<Record<string, unknown>> = [],
  options: { pages?: Array<Array<Record<string, unknown>>> } = {},
): typeof fetch {
  const pages = options.pages;
  return async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const method = (init?.method ?? "GET").toUpperCase();
    if (url.includes("/app/installations/") && method === "POST") {
      return Response.json({ token: "ghs_install" });
    }
    if (url.includes("/issues/") && method === "GET" && !url.includes("?")) {
      const number = Number(url.split("/issues/")[1]);
      const issue = issues.find((item) => item["number"] === number);
      if (!issue) {
        return Response.json({ message: "Not Found" }, { status: 404 });
      }
      return Response.json(issue);
    }
    if (url.includes("/issues") && method === "GET") {
      if (pages) {
        const parsed = new URL(url, "https://api.github.com");
        const pageIndex = parsed.searchParams.get("page") === "2" ? 1 : 0;
        const next =
          pageIndex === 0 && pages[1]
            ? `<https://api.github.com/repos/acme/demo/issues?per_page=100&state=open&page=2>; rel="next"`
            : null;
        return Response.json(pages[pageIndex] ?? [], {
          headers: next ? { link: next } : undefined,
        });
      }
      return Response.json(issues);
    }
    if (url.includes("/pulls") && method === "GET") {
      return Response.json(
        issues.filter((item) => item["pull_request"]).map((item) => ({ ...item, draft: false })),
      );
    }
    if (method !== "GET") {
      throw new Error(`unexpected GitHub write ${method} ${url}`);
    }
    return Response.json({ message: "Not Found" }, { status: 404 });
  };
}

describe("GitHub webhooks and import", () => {
  it("rejects an unsigned webhook", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({ store, config: testConfig(), checkReady: async () => true });
    const res = await app.request("/v1/webhooks/github", {
      method: "POST",
      headers: { "content-type": "application/json", "x-github-event": "issues" },
      body: JSON.stringify({ action: "opened" }),
    });
    expect(res.status).toBe(401);
    expect(await res.json()).toMatchObject({ error: { code: "unauthorized" } });
  });

  it("enqueues import for installed-repo issue events when import is on", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const project = await createProject(alice.app, alice.token, "gh-import");
    const repo = await createGithubRepo(alice.app, alice.token, project.id, {
      issues: "import",
    });

    const payload = JSON.stringify({
      action: "opened",
      installation: { id: 77 },
      repository: { id: 4242 },
      issue: { id: 9001, number: 12, title: "Broken login", pull_request: undefined },
    });
    const res = await alice.app.request("/v1/webhooks/github", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "issues",
        "x-hub-signature-256": signWebhook(payload),
      },
      body: payload,
    });
    expect(res.status).toBe(202);
    expect(jobs.githubImportJobs).toHaveLength(1);
    expect(jobs.githubImportJobs[0]?.data).toEqual({
      repo_id: repo.id,
      project_id: project.id,
      issue_number: 12,
    });
  });

  it("does not enqueue import when github.issues is off", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const project = await createProject(alice.app, alice.token, "gh-off");
    await createGithubRepo(alice.app, alice.token, project.id);

    const payload = JSON.stringify({
      action: "opened",
      installation: { id: 77 },
      repository: { id: 4242 },
      issue: { id: 1, number: 1, title: "Nope" },
    });
    const res = await alice.app.request("/v1/webhooks/github", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "issues",
        "x-hub-signature-256": signWebhook(payload),
      },
      body: payload,
    });
    expect(res.status).toBe(202);
    expect(jobs.githubImportJobs).toHaveLength(0);
  });

  it("accepts push webhooks without enqueueing clone invalidation", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const project = await createProject(alice.app, alice.token, "gh-push");
    const repo = await createGithubRepo(alice.app, alice.token, project.id);

    const payload = JSON.stringify({
      ref: "refs/heads/main",
      before: "aaa",
      after: "bbb",
      installation: { id: 77 },
      repository: { id: 4242 },
    });
    const res = await alice.app.request("/v1/webhooks/github", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "push",
        "x-hub-signature-256": signWebhook(payload),
      },
      body: payload,
    });
    expect(res.status).toBe(202);
    expect(jobs.githubInvalidateJobs).toEqual([]);

    const recorded = await alice.app.request(`/v1/repos/${repo.id}/github/invalidations`, {
      method: "POST",
      headers: { authorization: `Bearer ${WORKER_TOKEN}` },
    });
    expect(recorded.status).toBe(404);
  });

  it("ignores events for repos that are not installed", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const payload = JSON.stringify({
      installation: { id: 99 },
      repository: { id: 1 },
      issue: { number: 3 },
    });
    const res = await alice.app.request("/v1/webhooks/github", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "issues",
        "x-hub-signature-256": signWebhook(payload),
      },
      body: payload,
    });
    expect(res.status).toBe(202);
    expect(jobs.githubImportJobs).toHaveLength(0);
  });

  it("lists issues through the GitHub App and links one to a task", async () => {
    const store = new MemoryAuthStore();
    const issue = {
      id: 9001,
      number: 12,
      title: "Broken login",
      body: "users cannot sign in",
      state: "open",
      html_url: "https://github.com/acme/demo/issues/12",
    };
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      githubFetch: githubFetchImpl([issue]),
    });
    const registered = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "alice", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(registered)!;
    const project = await createProject(app, token, "gh-link");
    const repo = await createGithubRepo(app, token, project.id, { issues: "import" });
    const projectRecord = await store.findProjectById(project.id);
    await store.upsertGithubInstallation({
      id: "018f1e2c-3d4e-7000-8000-000000000088",
      orgId: projectRecord!.orgId,
      installationId: 77n,
      accountLogin: "acme",
      createdAt: new Date("2026-01-01T00:00:00.000Z"),
    });
    const taskRes = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "task-1",
      },
      body: JSON.stringify({ title: "Fix login" }),
    });
    const task = (await taskRes.json()) as { id: string };

    const listed = await app.request(`/v1/repos/${repo.id}/github/issues?state=open`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toMatchObject({
      items: [expect.objectContaining({ number: 12, title: "Broken login" })],
    });

    const linked = await app.request(`/v1/tasks/${task.id}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 12, repo_id: repo.id }),
    });
    expect(linked.status).toBe(200);
    expect(await linked.json()).toMatchObject({
      task_id: task.id,
      github_issue_id: "9001",
      issue_number: 12,
    });
    const stored = await store.findTaskById(task.id);
    expect(stored?.githubIssueId).toBe(9001n);
    const fetched = await app.request(`/v1/tasks/${task.id}`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(fetched.status).toBe(200);
    expect(await fetched.json()).toMatchObject({ github_issue_id: "9001" });

    const relinked = await app.request(`/v1/tasks/${task.id}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 12, repo_id: repo.id }),
    });
    expect(relinked.status).toBe(200);
    expect((await store.findTaskById(task.id))?.githubIssueId).toBe(9001n);

    const otherRes = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "task-2",
      },
      body: JSON.stringify({ title: "Also login" }),
    });
    const other = (await otherRes.json()) as { id: string };
    const conflict = await app.request(`/v1/tasks/${other.id}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 12, repo_id: repo.id }),
    });
    expect(conflict.status).toBe(409);
    expect(await conflict.json()).toMatchObject({
      error: { code: "login_taken", details: { reason: "unique" } },
    });
    expect((await store.findTaskById(other.id))?.githubIssueId).toBeNull();
  });

  it("persists githubIssueId through MemoryAuthStore.updateTask", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-01-01T00:00:00.000Z");
    const task = await store.createTask({
      id: "018f1e2c-3d4e-7000-8000-0000000000aa",
      projectId: "018f1e2c-3d4e-7000-8000-000000000010",
      milestoneId: null,
      parentId: null,
      title: "Fix login",
      description: "",
      status: "ready",
      priority: 0,
      type: "bug",
      version: 1,
      assigneeUserId: null,
      assigneeAgentName: null,
      agentBrief: "",
      howToCheck: "",
      linkedPaths: [],
      githubIssueId: null,
      lockedBySessionId: null,
      lockExpiresAt: null,
      deletedAt: null,
      createdAt: now,
      updatedAt: now,
    });
    const updated = await store.updateTask(task.id, task.version, { githubIssueId: 9001n }, now);
    expect(updated?.task.githubIssueId).toBe(9001n);
    expect((await store.findTaskById(task.id))?.githubIssueId).toBe(9001n);

    const other = await store.createTask({
      ...task,
      id: "018f1e2c-3d4e-7000-8000-0000000000bb",
      title: "Other",
    });
    await expect(
      store.updateTask(other.id, other.version, { githubIssueId: 9001n }, now),
    ).rejects.toMatchObject({ name: "UniqueViolationError" });
    expect((await store.findTaskById(other.id))?.githubIssueId).toBeNull();
  });

  it("returns the same 404 for a missing task and a non-member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "hidden");
    const repo = await createGithubRepo(alice.app, alice.token, project.id);
    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "hidden-task",
      },
      body: JSON.stringify({ title: "Secret" }),
    });
    const taskId = ((await taskRes.json()) as { id: string }).id;

    const missing = await alice.app.request(
      "/v1/tasks/00000000-0000-7000-8000-000000000099/github-issue",
      {
        method: "POST",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({ issue_number: 1, repo_id: repo.id }),
      },
    );
    const hidden = await bob.app.request(`/v1/tasks/${taskId}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 1, repo_id: repo.id }),
    });
    expect(missing.status).toBe(404);
    expect(hidden.status).toBe(404);
    expect(await missing.json()).toMatchObject({
      error: { code: "not_found", message: "task not found" },
    });
    expect(await hidden.json()).toMatchObject({
      error: { code: "not_found", message: "task not found" },
    });
  });

  it("lets the worker upsert imported issues idempotently and never writes GitHub", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "worker-import");
    await alice.app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "worker-import" }),
    });
    const repo = await createGithubRepo(alice.app, alice.token, project.id, {
      issues: "import",
    });

    const body = {
      issues: [
        {
          github_issue_id: "9001",
          number: 12,
          title: "Broken login",
          body: "users cannot sign in",
        },
      ],
      cursor: "cursor-1",
    };
    const first = await alice.app.request(`/v1/repos/${repo.id}/github/imported-issues`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${WORKER_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify(body),
    });
    expect(first.status).toBe(200);
    const created = (await first.json()) as {
      items: Array<{ id: string; github_issue_id: string }>;
    };
    expect(created.items[0]?.github_issue_id).toBe("9001");

    const second = await alice.app.request(`/v1/repos/${repo.id}/github/imported-issues`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${WORKER_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({
        issues: [
          {
            github_issue_id: "9001",
            number: 12,
            title: "Broken login (updated)",
            body: "still broken",
          },
        ],
        cursor: "cursor-2",
      }),
    });
    expect(second.status).toBe(200);
    const updated = (await second.json()) as { items: Array<{ id: string; title: string }> };
    expect(updated.items[0]?.id).toBe(created.items[0]?.id);
    expect(updated.items[0]?.title).toBe("Broken login (updated)");

    const cookieWrite = await alice.app.request(`/v1/repos/${repo.id}/github/imported-issues`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    expect(cookieWrite.status).toBe(404);

    const sync = await alice.app.request(`/v1/repos/${repo.id}/github/sync`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(sync.status).toBe(202);

    const state = await alice.app.request(`/v1/repos/${repo.id}/github/sync-state`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(await state.json()).toMatchObject({ repo_id: repo.id, last_cursor: "cursor-2" });
  });

  it("returns repo_ambiguous when repo_id is omitted and no default exists", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "ambiguous");
    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "amb-1",
      },
      body: JSON.stringify({ title: "No repo" }),
    });
    const taskId = ((await taskRes.json()) as { id: string }).id;
    const res = await alice.app.request(`/v1/tasks/${taskId}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 1 }),
    });
    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: { code: "repo_ambiguous" } });
  });

  it("turns github.issues on through PATCH settings", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "settings-on");
    await createGithubRepo(alice.app, alice.token, project.id);
    const patched = await alice.app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ settings: { github: { issues: "import" } } }),
    });
    expect(patched.status).toBe(200);
    expect(await patched.json()).toMatchObject({
      settings: { github: { issues: "import" } },
    });
    const stored = await store.findProjectById(project.id);
    expect(stored?.settings).toEqual({ github: { issues: "import" } });
    const twoWay = await alice.app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ settings: { github: { issues: "two_way" } } }),
    });
    expect(twoWay.status).toBe(400);
    expect(await twoWay.json()).toMatchObject({
      error: { details: { reason: "github_two_way_off" } },
    });
  });

  it("persists github_installations from repo register and installation webhooks", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "install-persist");
    await createGithubRepo(alice.app, alice.token, project.id, { installationId: 88 });
    const fromRepo = await store.findGithubInstallationByInstallationId(88n);
    expect(fromRepo).toMatchObject({ installationId: 88n, orgId: (await store.findProjectById(project.id))?.orgId });

    const payload = JSON.stringify({
      action: "created",
      installation: { id: 91, account: { login: "acme" } },
      repositories: [{ id: 4242 }],
    });
    const res = await alice.app.request("/v1/webhooks/github", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "installation",
        "x-hub-signature-256": signWebhook(payload),
      },
      body: payload,
    });
    expect(res.status).toBe(202);
    const fromWebhook = await store.findGithubInstallationByInstallationId(91n);
    expect(fromWebhook).toMatchObject({ installationId: 91n, accountLogin: "acme" });
  });

  it("reuses an existing task when two imported issues race on the same github_issue_id", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "race-import");
    const repo = await createGithubRepo(alice.app, alice.token, project.id, { issues: "import" });
    const body = {
      issues: [
        {
          github_issue_id: "9001",
          number: 12,
          title: "Broken login",
          body: "users cannot sign in",
        },
      ],
    };
    const [first, second] = await Promise.all([
      alice.app.request(`/v1/repos/${repo.id}/github/imported-issues`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${WORKER_TOKEN}`,
          "content-type": "application/json",
        },
        body: JSON.stringify(body),
      }),
      alice.app.request(`/v1/repos/${repo.id}/github/imported-issues`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${WORKER_TOKEN}`,
          "content-type": "application/json",
        },
        body: JSON.stringify(body),
      }),
    ]);
    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    const firstBody = (await first.json()) as { items: Array<{ id: string }> };
    const secondBody = (await second.json()) as { items: Array<{ id: string }> };
    expect(firstBody.items[0]?.id).toBe(secondBody.items[0]?.id);
    const linked = (await store.listTasks(project.id)).filter((task) => task.githubIssueId === 9001n);
    expect(linked).toHaveLength(1);
  });

  it("returns the same 404 for a missing repo and an unauthorized project", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "hidden-repo");
    const repo = await createGithubRepo(alice.app, alice.token, project.id);
    const missing = await alice.app.request(
      "/v1/repos/00000000-0000-7000-8000-000000000099/github/issues",
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    const hidden = await bob.app.request(`/v1/repos/${repo.id}/github/issues`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(missing.status).toBe(404);
    expect(hidden.status).toBe(404);
    expect(await missing.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });
    expect(await hidden.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });
  });

  it("keeps 403 when a member lacks integrations:write", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const reader = await registerUser(store, "reader");
    const project = await createProject(alice.app, alice.token, "role-repo");
    const repo = await createGithubRepo(alice.app, alice.token, project.id, { issues: "import" });
    await alice.app.request(`/v1/projects/${project.id}/members`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ user_id: reader.user.id, role: "read" }),
    });
    const res = await reader.app.request(`/v1/repos/${repo.id}/github/sync`, {
      method: "POST",
      headers: { cookie: cookieHeader(reader.token) },
    });
    expect(res.status).toBe(403);
    expect(await res.json()).toMatchObject({ error: { code: "forbidden" } });
  });

  it("paginates GitHub issues past the first 100 and filters PRs by linked task", async () => {
    const store = new MemoryAuthStore();
    const firstPage = Array.from({ length: 100 }, (_, index) => ({
      id: 1000 + index,
      number: index + 1,
      title: `Issue ${index + 1}`,
      body: "",
      state: "open",
      html_url: `https://github.com/acme/demo/issues/${index + 1}`,
    }));
    const secondPage = [
      {
        id: 9001,
        number: 12,
        title: "Broken login",
        body: "users cannot sign in",
        state: "open",
        html_url: "https://github.com/acme/demo/issues/12",
      },
    ];
    const pulls = [
      {
        id: 55,
        number: 3,
        title: "Fix login",
        body: "Closes #12",
        state: "open",
        html_url: "https://github.com/acme/demo/pull/3",
        pull_request: {},
      },
      {
        id: 56,
        number: 4,
        title: "Unrelated",
        body: "no mention",
        state: "open",
        html_url: "https://github.com/acme/demo/pull/4",
        pull_request: {},
      },
    ];
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      githubFetch: githubFetchImpl([...firstPage, ...secondPage, ...pulls], {
        pages: [firstPage, secondPage],
      }),
    });
    const registered = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "alice", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(registered)!;
    const project = await createProject(app, token, "gh-pages");
    const repo = await createGithubRepo(app, token, project.id, { issues: "import" });
    const listed = await app.request(`/v1/repos/${repo.id}/github/issues?state=open`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(listed.status).toBe(200);
    const page = (await listed.json()) as { items: unknown[]; next_cursor: string | null };
    expect(page.items).toHaveLength(100);
    expect(page.next_cursor).toBeTruthy();
    const next = await app.request(
      `/v1/repos/${repo.id}/github/issues?state=open&cursor=${page.next_cursor}`,
      { headers: { cookie: cookieHeader(token) } },
    );
    expect(next.status).toBe(200);
    expect(await next.json()).toMatchObject({
      items: [expect.objectContaining({ number: 12, title: "Broken login" })],
      next_cursor: null,
    });

    const taskRes = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "page-task",
      },
      body: JSON.stringify({ title: "Fix login" }),
    });
    const task = (await taskRes.json()) as { id: string };
    await app.request(`/v1/tasks/${task.id}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 12, repo_id: repo.id }),
    });
    const prs = await app.request(`/v1/repos/${repo.id}/github/pulls?task_id=${task.id}`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(prs.status).toBe(200);
    expect(await prs.json()).toMatchObject({
      items: [expect.objectContaining({ number: 3, title: "Fix login" })],
    });
  });

  it("returns matched_files on /v1/tasks/:id/github-prs", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      githubFetch: githubFetchImplWithFiles(),
    });
    const registered = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "bob", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(registered)!;
    const project = await createProject(app, token, "matched-files");
    const repo = await createGithubRepo(app, token, project.id, { issues: "import" });
    const taskRes = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "task-with-linked",
      },
      body: JSON.stringify({
        title: "Update API handler",
      }),
    });
    const task = (await taskRes.json()) as { id: string };
    expect(taskRes.status).toBe(201);
    // Update task with linked_paths
    await app.request(`/v1/tasks/${task.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ linked_paths: [{ path: "src/handlers", repo_id: repo.id }], expected_version: 1 }),
    });
    await app.request(`/v1/tasks/${task.id}/github-issue`, {
      method: "POST",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ issue_number: 5, repo_id: repo.id }),
    });
    const res = await app.request(`/v1/tasks/${task.id}/github-prs`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { items: { matched_files?: string[]; files_changed?: string[]; number: number; body?: string }[] };
    // Verify the response has data
    expect(body.items.length).toBeGreaterThanOrEqual(1);
    const pr = body.items[0]!;
    expect(pr.number).toBe(5);
    expect(pr.files_changed).toContain("src/handlers/api.ts");
    expect(pr.matched_files).toContain("src/handlers/api.ts");
    expect(pr.matched_files).not.toContain("src/utils/helpers.ts");
  });

  it("returns diff-aware tasks for a PR via /v1/repos/:id/github/pulls/:number/tasks", async () => {
    const store = new MemoryAuthStore();
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      githubFetch: githubFetchImplWithFiles(),
    });
    const registered = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "charlie", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(registered)!;
    const project = await createProject(app, token, "diff-aware");
    const repo = await createGithubRepo(app, token, project.id, { issues: "import" });
    // Create task with linked_paths matching PR files
    const taskRes = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "diff-task-1",
      },
      body: JSON.stringify({ title: "Handler refactor" }),
    });
    const task = (await taskRes.json()) as { id: string };
    expect(taskRes.status).toBe(201);
    await app.request(`/v1/tasks/${task.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ linked_paths: [{ path: "src/handlers", repo_id: repo.id }], expected_version: 1 }),
    });
    // Create a second task with linked_paths NOT matching PR files
    const task2Res = await app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(token),
        "content-type": "application/json",
        "idempotency-key": "diff-task-2",
      },
      body: JSON.stringify({ title: "Frontend polish" }),
    });
    const task2 = (await task2Res.json()) as { id: string };
    expect(task2Res.status).toBe(201);
    await app.request(`/v1/tasks/${task2.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token), "content-type": "application/json" },
      body: JSON.stringify({ linked_paths: [{ path: "src/frontend", repo_id: repo.id }], expected_version: 1 }),
    });
    // Query PR tasks — only the task matching "src/handlers" should be returned
    const res = await app.request(`/v1/repos/${repo.id}/github/pulls/5/tasks`, {
      headers: { cookie: cookieHeader(token) },
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { items: { task_id: string; title: string; status: string; matched_files: string[] }[]; next_cursor: string | null };
    expect(body.next_cursor).toBeNull();
    expect(body.items.length).toBe(1);
    const matched = body.items[0]!;
    expect(matched.task_id).toBe(task.id);
    expect(matched.title).toBe("Handler refactor");
    expect(matched.status).toBe("backlog");
    expect(matched.matched_files).toContain("src/handlers/api.ts");
    expect(matched.matched_files).not.toContain("src/utils/helpers.ts");
  });
});

function githubFetchImplWithFiles(): typeof fetch {
  const issues = [
    {
      id: 8001,
      number: 5,
      title: "Update API handler",
      body: "fix the handler logic",
      state: "open",
      html_url: "https://github.com/acme/demo/issues/5",
    },
  ];
  const pulls = [
    {
      id: 7001,
      number: 5,
      title: "Update API handler",
      body: "Closes #5",
      state: "open",
      html_url: "https://github.com/acme/demo/pull/5",
      pull_request: {},
    },
  ];
  const files = [
    { filename: "src/handlers/api.ts" },
    { filename: "src/utils/helpers.ts" },
  ];
  return async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const method = (init?.method ?? "GET").toUpperCase();
    if (url.includes("/app/installations/") && method === "POST") {
      return Response.json({ token: "ghs_install" });
    }
    if (url.includes("/issues/") && method === "GET" && !url.includes("?")) {
      const number = Number(url.split("/issues/")[1]);
      const issue = issues.find((item) => item["number"] === number);
      if (!issue) {
        return Response.json({ message: "Not Found" }, { status: 404 });
      }
      return Response.json(issue);
    }
    if (url.includes("/pulls") && method === "GET" && !url.includes("/files")) {
      return Response.json(
        pulls.filter((item) => item["pull_request"]).map((item) => ({ ...item, draft: false })),
      );
    }
    if (url.includes("/pulls/") && url.includes("/files") && method === "GET") {
      return Response.json(files);
    }
    if (url.includes("/issues") && method === "GET") {
      return Response.json(issues);
    }
    if (method !== "GET") {
      throw new Error(`unexpected GitHub write ${method} ${url}`);
    }
    return Response.json({ message: "Not Found" }, { status: 404 });
  };
}
