import { createHmac, generateKeyPairSync } from "node:crypto";

import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { MemoryJobQueue } from "./jobs/queue.js";
import { uuidv7 } from "@beacon/shared";

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
    githubTwoWay: false,
    secureCookies: false,
    trustProxy: false,
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
  store: MemoryAuthStore,
  app: ReturnType<typeof createApp>,
  token: string,
  projectId: string,
  options: { issues?: "import" | "off" } = {},
) {
  const created = await app.request(`/v1/projects/${projectId}/repos`, {
    method: "POST",
    headers: { cookie: cookieHeader(token), "content-type": "application/json" },
    body: JSON.stringify({
      provider: "github",
      remote_url: "https://github.com/acme/demo",
      github_repo_id: 4242,
      installation_id: 77,
    }),
  });
  const repo = (await created.json()) as { id: string };
  const project = await store.findProjectById(projectId);
  if (!project) {
    throw new Error("project missing");
  }
  await store.upsertGithubInstallation({
    id: uuidv7(),
    orgId: project.orgId,
    installationId: 77n,
    accountLogin: "acme",
    createdAt: new Date(),
  });
  if (options.issues === "import") {
    await store.updateProjectSettings(projectId, { github: { issues: "import" } }, new Date());
  }
  return repo;
}

function githubFetchImpl(issues: Array<Record<string, unknown>> = []): typeof fetch {
  return async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const method = (init?.method ?? "GET").toUpperCase();
    if (url.includes("/app/installations/") && method === "POST") {
      return Response.json({ token: "ghs_install" });
    }
    if (url.includes("/issues/") && method === "GET") {
      const number = Number(url.split("/issues/")[1]);
      const issue = issues.find((item) => item["number"] === number);
      if (!issue) {
        return Response.json({ message: "Not Found" }, { status: 404 });
      }
      return Response.json(issue);
    }
    if (url.includes("/issues") && method === "GET") {
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
    const repo = await createGithubRepo(store, alice.app, alice.token, project.id, {
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
    await createGithubRepo(store, alice.app, alice.token, project.id);

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

  it("records hosted-clone invalidation for push without cloning", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const project = await createProject(alice.app, alice.token, "gh-push");
    const repo = await createGithubRepo(store, alice.app, alice.token, project.id);

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
    expect(jobs.githubInvalidateJobs).toEqual([
      expect.objectContaining({
        data: {
          repo_id: repo.id,
          project_id: project.id,
          ref: "refs/heads/main",
          before: "aaa",
          after: "bbb",
        },
      }),
    ]);
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
    const repo = await createGithubRepo(store, app, token, project.id, { issues: "import" });
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
  });

  it("returns the same 404 for a missing task and a non-member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "hidden");
    const repo = await createGithubRepo(store, alice.app, alice.token, project.id);
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
    const repo = await createGithubRepo(store, alice.app, alice.token, project.id, {
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
});
