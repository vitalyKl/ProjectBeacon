import { DEFAULT_PROJECT_LABELS } from "@beacon/context";
import { uuidv7 } from "@beacon/shared";
import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";
import { extraCompilePaths } from "./labels/scope.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    workerToken: undefined,
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
  return (await created.json()) as { id: string };
}

async function mintToken(
  app: ReturnType<typeof createApp>,
  cookie: string,
  projectId: string,
  scopes?: string[],
) {
  const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
    method: "POST",
    headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
    body: JSON.stringify({ name: "agent", ...(scopes ? { scopes } : {}) }),
  });
  return (await minted.json()) as { token: string };
}

type PublicLabel = {
  id: string;
  slug: string;
  name: string;
  status: string;
  paths: { repo_id: string; path: string }[];
};

describe("project labels", () => {
  it("seeds an editable starter catalog and lets a human attach an area to a task", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "areas");
    const repoRes = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "sidecar",
        local_root_hint: ".",
      }),
    });
    expect(repoRes.status).toBe(201);
    const repo = (await repoRes.json()) as { id: string };

    const listed = await alice.app.request(`/v1/projects/${project.id}/labels`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    const catalog = (await listed.json()) as { items: PublicLabel[] };
    expect(catalog.items.map((item) => item.slug).sort()).toEqual(
      DEFAULT_PROJECT_LABELS.map((item) => item.slug).sort(),
    );
    expect(catalog.items.every((item) => item.status === "active" && item.paths.length === 0)).toBe(
      true,
    );
    const seededApi = catalog.items.find((item) => item.slug === "api");
    expect(seededApi).toBeDefined();

    const patched = await alice.app.request(`/v1/labels/${seededApi!.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        color: "#3366ff",
        paths: [{ repo_id: repo.id, path: "/apps/api/" }],
      }),
    });
    expect(patched.status).toBe(200);
    const label = (await patched.json()) as PublicLabel;
    expect(label).toMatchObject({
      slug: "api",
      name: "API",
      status: "active",
      paths: [{ repo_id: repo.id, path: "apps/api" }],
    });

    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "task-api",
      },
      body: JSON.stringify({ title: "Ship compile extras", label_ids: [label.id] }),
    });
    expect(taskRes.status).toBe(201);
    const task = (await taskRes.json()) as {
      id: string;
      labels: { id: string; slug: string }[];
    };
    expect(task.labels).toEqual([expect.objectContaining({ id: label.id, slug: "api" })]);

    const replay = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "task-api",
      },
      body: JSON.stringify({ title: "Ship compile extras", label_ids: [label.id] }),
    });
    expect(replay.status).toBe(201);
    const replayed = (await replay.json()) as {
      id: string;
      labels: { id: string; slug: string }[];
    };
    expect(replayed).toEqual(task);

    const filtered = await alice.app.request(
      `/v1/projects/${project.id}/tasks?label_id=${label.id}`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    expect(filtered.status).toBe(200);
    expect(await filtered.json()).toMatchObject({
      items: [expect.objectContaining({ id: task.id })],
    });
  });

  it("forces a non-admin token to propose and rejects a slug collision", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "propose");
    const minted = await mintToken(alice.app, alice.token, project.id);

    const proposed = await alice.app.request(`/v1/projects/${project.id}/labels`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ name: "Docs", status: "active" }),
    });
    expect(proposed.status).toBe(201);
    const label = (await proposed.json()) as PublicLabel;
    expect(label).toMatchObject({ slug: "docs", status: "proposed" });

    const patched = await alice.app.request(`/v1/labels/${label.id}`, {
      method: "PATCH",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ status: "active" }),
    });
    expect(patched.status).toBe(403);

    const activated = await alice.app.request(`/v1/labels/${label.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ status: "active" }),
    });
    expect(activated.status).toBe(200);
    expect(await activated.json()).toMatchObject({ status: "active" });

    const collision = await alice.app.request(`/v1/projects/${project.id}/labels`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "Docs 2", slug: "docs" }),
    });
    expect(collision.status).toBe(409);
    expect(await collision.json()).toMatchObject({
      error: { code: "login_taken", details: { reason: "slug_taken" } },
    });
  });

  it("expands compile extra_paths from attached active labels only", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "compile-areas");
    const repoRes = await alice.app.request(`/v1/projects/${project.id}/repos`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        provider: "local",
        index_mode: "sidecar",
        local_root_hint: ".",
      }),
    });
    const repo = (await repoRes.json()) as { id: string };

    const listed = await alice.app.request(`/v1/projects/${project.id}/labels`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    const catalog = (await listed.json()) as { items: PublicLabel[] };
    const apiSeed = catalog.items.find((item) => item.slug === "api");
    const visualSeed = catalog.items.find((item) => item.slug === "visual");
    expect(apiSeed && visualSeed).toBeTruthy();

    const apiLabelRes = await alice.app.request(`/v1/labels/${apiSeed!.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        paths: [{ repo_id: repo.id, path: "apps/api" }],
      }),
    });
    const apiLabel = (await apiLabelRes.json()) as PublicLabel;
    const visualRes = await alice.app.request(`/v1/labels/${visualSeed!.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        status: "proposed",
        paths: [{ repo_id: repo.id, path: "apps/web" }],
      }),
    });
    const visual = (await visualRes.json()) as PublicLabel;

    const nodeId = uuidv7();
    const nodeRes = await alice.app.request(`/v1/projects/${project.id}/context/nodes/${nodeId}`, {
      method: "PUT",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        scope_type: "path",
        path: "apps/api",
        repo_id: repo.id,
        sections: [
          { id: "conventions", title: "Conventions", body_md: "Keep the API thin.", ordinal: 0 },
        ],
      }),
    });
    expect(nodeRes.status).toBe(200);

    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "compile-label",
      },
      body: JSON.stringify({ title: "Scope the API", label_ids: [apiLabel.id, visual.id] }),
    });
    const task = (await taskRes.json()) as { id: string };
    const attached = await store.listTaskLabels(task.id);
    expect(extraCompilePaths(attached, repo.id)).toEqual(["apps/api"]);

    const compiled = await alice.app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ project_id: project.id, task_id: task.id, repo_id: repo.id }),
    });
    expect(compiled.status).toBe(200);
    const brief = (await compiled.json()) as {
      sections: { id: string; body_md: string }[];
      sources: { node_id: string }[];
    };
    expect(brief.sections.find((section) => section.id === "conventions")?.body_md).toBe(
      "Keep the API thin.",
    );
    expect(brief.sources.map((source) => source.node_id)).toContain(nodeId);
  });

  it("backfills starter areas only onto projects with an empty catalog", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const seeded = await createProject(alice.app, alice.token, "already-seeded");
    const orgId = (await store.findProjectById(seeded.id))!.orgId;
    const beforeSeeded = await store.listLabels(seeded.id);
    expect(beforeSeeded.map((item) => item.slug).sort()).toEqual(
      DEFAULT_PROJECT_LABELS.map((item) => item.slug).sort(),
    );

    const emptyCreatedAt = new Date("2025-01-01T00:00:00.000Z");
    const emptyId = uuidv7(emptyCreatedAt.getTime());
    store.seedProject(
      {
        id: emptyId,
        orgId,
        slug: "pre-seed",
        name: "pre-seed",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: null,
        createdAt: emptyCreatedAt,
        updatedAt: emptyCreatedAt,
      },
      alice.user.id,
    );
    expect(await store.listLabels(emptyId)).toEqual([]);

    const deletedCreatedAt = new Date("2024-12-01T00:00:00.000Z");
    const deletedId = uuidv7(deletedCreatedAt.getTime());
    store.seedProject(
      {
        id: deletedId,
        orgId,
        slug: "trashed-empty",
        name: "trashed-empty",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: deletedCreatedAt,
        createdAt: deletedCreatedAt,
        updatedAt: deletedCreatedAt,
      },
      alice.user.id,
    );
    expect(await store.listLabels(deletedId)).toEqual([]);

    const customCreatedAt = new Date("2025-02-01T00:00:00.000Z");
    const customId = uuidv7(customCreatedAt.getTime());
    store.seedProject(
      {
        id: customId,
        orgId,
        slug: "custom-catalog",
        name: "custom-catalog",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: null,
        createdAt: customCreatedAt,
        updatedAt: customCreatedAt,
      },
      alice.user.id,
    );
    await store.createLabel({
      id: uuidv7(customCreatedAt.getTime() + 1),
      projectId: customId,
      slug: "docs",
      name: "Docs",
      description: "Custom only",
      color: "#111111",
      status: "active",
      createdAt: customCreatedAt,
      paths: [],
    });

    expect(await store.backfillEmptyProjectLabelCatalogs()).toBe(1);

    const emptyCatalog = await store.listLabels(emptyId);
    expect(emptyCatalog.map((item) => item.slug).sort()).toEqual(
      DEFAULT_PROJECT_LABELS.map((item) => item.slug).sort(),
    );
    expect(emptyCatalog.every((item) => item.status === "active" && item.paths.length === 0)).toBe(
      true,
    );

    const afterSeeded = await store.listLabels(seeded.id);
    expect(afterSeeded.map((item) => item.id).sort()).toEqual(
      beforeSeeded.map((item) => item.id).sort(),
    );

    const customCatalog = await store.listLabels(customId);
    expect(customCatalog.map((item) => item.slug)).toEqual(["docs"]);
    expect(await store.listLabels(deletedId)).toEqual([]);

    expect(await store.backfillEmptyProjectLabelCatalogs()).toBe(0);
    expect((await store.listLabels(emptyId)).map((item) => item.id).sort()).toEqual(
      emptyCatalog.map((item) => item.id).sort(),
    );
  });
});
