import { describe, expect, it } from "vitest";
import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { parseLocalRootHint } from "./repos/local-root.js";
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

describe("parseLocalRootHint", () => {
  it("accepts a relative POSIX path and rejects traversal or absolute paths", () => {
    expect(parseLocalRootHint("apps/web")).toBe("apps/web");
    expect(parseLocalRootHint("./apps/web")).toBeUndefined();
    expect(parseLocalRootHint(".")).toBe(".");
    expect(parseLocalRootHint("./")).toBeUndefined();
    expect(parseLocalRootHint("/workspace/apps")).toBeUndefined();
    expect(parseLocalRootHint("../secret")).toBeUndefined();
    expect(parseLocalRootHint("C:/Windows")).toBeUndefined();
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
        local_root_hint: ".",
      }),
    });
    expect(created.status).toBe(201);
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

  it("registers a sidecar heartbeat and 503s code routes without an index", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "code");
    const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ provider: "local", index_mode: "sidecar", local_root_hint: "." }),
    });
    const repoId = ((await created.json()) as { id: string }).id;
    const minted = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "sidecar", scopes: ["project:read", "code:read"] }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const registered = await alice.app.request(`/v1/repos/${repoId}/sidecar/register`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(registered.status).toBe(200);

    const status = await alice.app.request(`/v1/repos/${repoId}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(await status.json()).toMatchObject({
      sidecar_connected: true,
      worker_index_connected: false,
    });

    const tree = await alice.app.request(`/v1/repos/${repoId}/tree`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(tree.status).toBe(503);
    expect(await tree.json()).toMatchObject({ error: { code: "code_index_unavailable" } });
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

  it("404s changed-scope when repo_id is present but unknown", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "scope");
    await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ provider: "local", local_root_hint: "demo" }),
    });
    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "scope-task",
      },
      body: JSON.stringify({ title: "Scope" }),
    });
    const taskId = ((await taskRes.json()) as { id: string }).id;
    const res = await alice.app.request(
      `/v1/tasks/${taskId}/changed-scope?repo_id=00000000-0000-7000-8000-000000000099`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    expect(res.status).toBe(404);
    expect(await res.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });
  });

  it("rejects hosted_clone when the operator flag is off", async () => {
    const previous = process.env["ff.hosted_clone"];
    delete process.env["ff.hosted_clone"];
    delete process.env.FF_HOSTED_CLONE;
    try {
      const store = new MemoryAuthStore();
      const alice = await registerUser(store, "alice");
      const project = await createProject(alice.app, alice.token, "flag-off");
      const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
        method: "POST",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          provider: "github",
          remote_url: "https://github.com/acme/demo",
          installation_id: "12",
          index_mode: "hosted_clone",
        }),
      });
      expect(created.status).toBe(400);
      expect(await created.json()).toMatchObject({
        error: { code: "unauthorized", message: "invalid index_mode" },
      });
    } finally {
      if (previous === undefined) {
        delete process.env["ff.hosted_clone"];
      } else {
        process.env["ff.hosted_clone"] = previous;
      }
    }
  });

  it("patches index_mode when hosted clone is enabled and consumes invalidation as worker", async () => {
    process.env["ff.hosted_clone"] = "true";
    try {
      const store = new MemoryAuthStore();
      const jobs = new MemoryJobQueue();
      const alice = await registerUser(store, "alice", jobs);
      const project = await createProject(alice.app, alice.token, "clone-on");
      const created = await alice.app.request(`/v1/projects/${project.id}/repos`, {
        method: "POST",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          provider: "github",
          remote_url: "https://github.com/acme/demo",
          installation_id: "12",
          index_mode: "sidecar",
        }),
      });
      expect(created.status).toBe(201);
      const repoId = ((await created.json()) as { id: string }).id;

      const patched = await alice.app.request(`/v1/repos/${repoId}`, {
        method: "PATCH",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({ index_mode: "hosted_clone" }),
      });
      expect(patched.status).toBe(200);
      expect(await patched.json()).toMatchObject({ index_mode: "hosted_clone" });
      expect(jobs.detectJobs).toHaveLength(1);

      store.seedCloneInvalidation({
        id: "01934567-89ab-7cde-89ab-0123456789cc",
        repoId,
        sha: "abc",
        createdAt: new Date("2026-01-01T00:00:00.000Z"),
      });
      const consumed = await alice.app.request(`/v1/repos/${repoId}/clone-invalidation/consume`, {
        method: "POST",
        headers: { authorization: `Bearer ${WORKER_TOKEN}` },
      });
      expect(consumed.status).toBe(200);
      expect(await consumed.json()).toMatchObject({
        consumed: { sha: "abc" },
      });
      const again = await alice.app.request(`/v1/repos/${repoId}/clone-invalidation/consume`, {
        method: "POST",
        headers: { authorization: `Bearer ${WORKER_TOKEN}` },
      });
      expect(await again.json()).toEqual({ consumed: null });

      await alice.app.request(`/v1/projects/${project.id}`, {
        method: "DELETE",
        headers: { cookie: cookieHeader(alice.token) },
      });
      const listed = await alice.app.request("/v1/internal/deleted-projects", {
        headers: { authorization: `Bearer ${WORKER_TOKEN}` },
      });
      expect(listed.status).toBe(200);
      expect(await listed.json()).toMatchObject({
        items: [{ id: project.id }],
      });
      const hidden = await alice.app.request("/v1/internal/deleted-projects", {
        headers: { cookie: cookieHeader(alice.token) },
      });
      expect(hidden.status).toBe(403);
    } finally {
      delete process.env["ff.hosted_clone"];
    }
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
