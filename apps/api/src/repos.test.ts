import { describe, expect, it } from "vitest";
import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { parseBindMountHint } from "./repos/routes.js";
import { MemoryJobQueue } from "./jobs/queue.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    workerToken: WORKER_TOKEN,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
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
  return (await created.json()) as { id: string; default_repo_id: string | null };
}

describe("parseBindMountHint", () => {
  it("accepts a relative POSIX path and rejects traversal or absolute paths", () => {
    expect(parseBindMountHint("apps/web")).toBe("apps/web");
    expect(parseBindMountHint("./apps/web")).toBe("apps/web");
    expect(parseBindMountHint(".")).toBe(".");
    expect(parseBindMountHint("./")).toBe(".");
    expect(parseBindMountHint("/workspace/apps")).toBeUndefined();
    expect(parseBindMountHint("../secret")).toBeUndefined();
    expect(parseBindMountHint("C:/Windows")).toBeUndefined();
  });
});

describe("project repos", () => {
  it("creates a bind-mount repo and sets default_repo_id on the first insert", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "repos");
    expect(project.default_repo_id).toBeNull();

    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: "apps/web",
      }),
    });
    expect(created.status).toBe(201);
    const repo = (await created.json()) as {
      id: string;
      provider: string;
      index_mode: string;
      local_root_hint: string;
      sidecar_connected: boolean;
      worker_index_connected: boolean;
    };
    expect(repo).toMatchObject({
      provider: "local",
      index_mode: "bind_mount",
      local_root_hint: "apps/web",
      sidecar_connected: false,
      worker_index_connected: false,
    });

    const listed = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    const page = (await listed.json()) as { items: { id: string }[] };
    expect(page.items).toHaveLength(1);
    expect(page.items[0]?.id).toBe(repo.id);

    const got = await alice.app.request(`/v1/repos/${repo.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(got.status).toBe(200);
    expect(await got.json()).toMatchObject({ id: repo.id, index_mode: "bind_mount" });

    const projectGet = await alice.app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(await projectGet.json()).toMatchObject({ default_repo_id: repo.id });
  });

  it("rejects an absolute bind-mount hint", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "abs");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: "/etc/passwd",
      }),
    });
    expect(created.status).toBe(400);
  });

  it("accepts the workspace root as a bind-mount hint", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "root");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: ".",
      }),
    });
    expect(created.status).toBe(201);
    expect(await created.json()).toMatchObject({ local_root_hint: "." });
  });

  it("maps a duplicate local root to 409", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "dup");
    const first = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: "apps/api",
      }),
    });
    expect(first.status).toBe(201);
    const second = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: "apps/api",
      }),
    });
    expect(second.status).toBe(409);
  });

  it("returns not found for a non-member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "sidecar",
      }),
    });
    const repo = (await created.json()) as { id: string };

    const listed = await bob.app.request(`/v1/projects/${project.id}/repos`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(listed.status).toBe(404);

    const got = await bob.app.request(`/v1/repos/${repo.id}`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(got.status).toBe(404);
    expect(await got.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });
  });

  it("keeps 403 when a same-project token lacks project:read", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "scoped");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "sidecar",
      }),
    });
    const repo = (await created.json()) as { id: string };
    const minted = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "tasks-only", scopes: ["tasks:read"] }),
    });
    expect(minted.status).toBe(201);
    const secret = ((await minted.json()) as { token: string }).token;

    const got = await alice.app.request(`/v1/repos/${repo.id}`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(got.status).toBe(403);
    expect(await got.json()).toMatchObject({ error: { code: "forbidden" } });
  });
});
const WORKER_TOKEN = "deploy-time-worker-token";

describe("project repos", () => {
  it("creates, lists, and gets a local repo and sets default_repo_id", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "repos");

    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: "demo/app",
      }),
    });
    expect(created.status).toBe(201);
    const repo = (await created.json()) as {
      id: string;
      provider: string;
      local_root_hint: string;
      index_mode: string;
    };
    expect(repo).toMatchObject({
      provider: "local",
      local_root_hint: "demo/app",
      index_mode: "bind_mount",
    });

    const listed = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    const page = (await listed.json()) as { items: { id: string }[] };
    expect(page.items).toHaveLength(1);

    const got = await alice.app.request(`/v1/repos/${repo.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(got.status).toBe(200);
    expect(await got.json()).toMatchObject({
      id: repo.id,
      last_indexed_sha: null,
      sidecar_connected: false,
      worker_index_connected: false,
    });

    const projectGet = await alice.app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(await projectGet.json()).toMatchObject({ default_repo_id: repo.id });
  });

  it("rejects absolute or parent local_root_hint", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "bad-root");
    for (const hint of ["/tmp/repo", "../escape", "foo/../bar"]) {
      const res = await alice.app.request(`/v1/projects/${project.id}/repos`, {
        method: "POST",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({ provider: "local", local_root_hint: hint }),
      });
      expect(res.status).toBe(400);
    }
  });

  it("returns 404 for unknown repo ids and non-members", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret-repo");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ provider: "local", local_root_hint: "demo" }),
    });
    const repoId = ((await created.json()) as { id: string }).id;

    const missing = await alice.app.request("/v1/repos/00000000-0000-7000-8000-000000000099", {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(missing.status).toBe(404);

    const hidden = await bob.app.request(`/v1/repos/${repoId}`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(hidden.status).toBe(404);
    expect(await hidden.json()).toMatchObject({ error: { code: "not_found" } });
  });
});

describe("POST /v1/repos/:id/detect", () => {
  it("enqueues a detect job and is idempotent with Idempotency-Key", async () => {
    const store = new MemoryAuthStore();
    const jobs = new MemoryJobQueue();
    const alice = await registerUser(store, "alice", jobs);
    const project = await createProject(alice.app, alice.token, "detect");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ provider: "local", local_root_hint: "demo" }),
    });
    const repoId = ((await created.json()) as { id: string }).id;

    const first = await alice.app.request(`/v1/repos/${repoId}/detect`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "idempotency-key": "detect-1" },
    });
    expect(first.status).toBe(202);
    const accepted = (await first.json()) as { id: string; status: string };
    expect(accepted.status).toBe("accepted");
    expect(jobs.detectJobs).toHaveLength(1);
    expect(jobs.detectJobs[0]?.data).toEqual({ repo_id: repoId, project_id: project.id });

    const second = await alice.app.request(`/v1/repos/${repoId}/detect`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "idempotency-key": "detect-1" },
    });
    expect(second.status).toBe(202);
    expect(await second.json()).toEqual(accepted);
    expect(jobs.detectJobs).toHaveLength(1);
  });

  it("lets BEACON_WORKER_TOKEN import context and create a milestone", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "worker-write");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ provider: "local", local_root_hint: "demo" }),
    });
    const repoId = ((await created.json()) as { id: string }).id;

    const imported = await alice.app.request(
      `/v1/projects/${project.id}/context/import?repo_id=${repoId}`,
      {
        method: "POST",
        headers: {
          authorization: `Bearer ${WORKER_TOKEN}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({
          files: [{ path: "AGENTS.md", content: "## Goals\nShip it.\n" }],
        }),
      },
    );
    expect(imported.status).toBe(200);

    const milestone = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${WORKER_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ title: "Detector skeleton" }),
    });
    expect(milestone.status).toBe(201);

    const listed = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(((await listed.json()) as { items: unknown[] }).items).toHaveLength(0);
  });
});
