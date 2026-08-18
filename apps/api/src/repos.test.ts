import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { parseBindMountHint } from "./repos/routes.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
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

async function registerUser(store: MemoryAuthStore, login: string) {
  const app = createApp({ store, config: testConfig(), checkReady: async () => true });
  const res = await app.request("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ login, password: STRONG_PASSWORD }),
  });
  const token = sessionCookie(res);
  const body = (await res.json()) as { id: string; login: string };
  return { app, token: token!, user: body };
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
});
