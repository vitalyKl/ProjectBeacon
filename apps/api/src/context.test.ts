import { describe, expect, it } from "vitest";
import { isUuidV7, uuidv7 } from "@beacon/shared";
import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";

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

describe("default security constraints", () => {
  it("seeds active security constraints when a project is created", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "seeded");
    const constraints = await store.listActiveConstraints(project.id);
    expect(constraints).toHaveLength(2);
    expect(constraints.every((item) => item.kind === "security" && item.status === "active")).toBe(
      true,
    );
    expect(constraints.map((item) => item.body)).toEqual(
      expect.arrayContaining([
        expect.stringContaining("unreviewed imported context files"),
        expect.stringContaining("Do not commit API tokens"),
      ]),
    );
  });
});

describe("GET /v1/projects/:id/context/nodes", () => {
  it("returns imported nodes for a project member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "nodes");

    const imported = await alice.app.request(`/v1/projects/${project.id}/context/import`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        files: [{ path: "AGENTS.md", content: "## Goals\nShip it.\n" }],
      }),
    });
    expect(imported.status).toBe(200);

    const res = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      items: { review_state: string; source: string; sections: { id: string }[] }[];
    };
    expect(body.items.length).toBeGreaterThan(0);
    expect(body.items[0]).toMatchObject({
      review_state: "needs_review",
      source: "imported_agents_md",
    });
    expect(body.items[0]?.sections.some((section) => section.id === "goals")).toBe(true);
  });
});

describe("PUT /v1/projects/:id/context/nodes/:nodeId", () => {
  it("creates a native project node and updates sections and review_state", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "edit-node");
    const nodeId = uuidv7();

    const created = await alice.app.request(`/v1/projects/${project.id}/context/nodes/${nodeId}`, {
      method: "PUT",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        scope_type: "project",
        path: "",
        sections: [{ id: "goals", title: "Goals", body_md: "Ship the brief.", ordinal: 0 }],
      }),
    });
    expect(created.status).toBe(200);
    const createdBody = (await created.json()) as {
      id: string;
      source: string;
      review_state: string;
      sections: { id: string; body_md: string }[];
    };
    expect(createdBody).toMatchObject({
      id: nodeId,
      source: "native",
      review_state: "reviewed",
    });
    expect(createdBody.sections[0]).toMatchObject({ id: "goals", body_md: "Ship the brief." });

    await alice.app.request(`/v1/projects/${project.id}/context/import`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        files: [{ path: "AGENTS.md", content: "## Conventions\nMatch routes.\n" }],
      }),
    });
    const listed = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    const items = (
      (await listed.json()) as {
        items: { id: string; source: string; review_state: string }[];
      }
    ).items;
    const imported = items.find((item) => item.source === "imported_agents_md");
    expect(imported?.review_state).toBe("needs_review");

    const reviewed = await alice.app.request(
      `/v1/projects/${project.id}/context/nodes/${imported!.id}`,
      {
        method: "PUT",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          review_state: "reviewed",
          sections: [
            { id: "conventions", title: "Conventions", body_md: "Keep it small.", ordinal: 0 },
          ],
        }),
      },
    );
    expect(reviewed.status).toBe(200);
    expect(await reviewed.json()).toMatchObject({
      id: imported!.id,
      review_state: "reviewed",
      sections: [{ id: "conventions", body_md: "Keep it small." }],
    });
  });

  it("returns 404 for a non-member or cross-project node", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const aliceProject = await createProject(alice.app, alice.token, "alice-ctx");
    const bobProject = await createProject(bob.app, bob.token, "bob-ctx");
    const aliceNode = uuidv7();
    const bobNode = uuidv7();

    expect(
      (
        await alice.app.request(`/v1/projects/${aliceProject.id}/context/nodes/${aliceNode}`, {
          method: "PUT",
          headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
          body: JSON.stringify({
            scope_type: "project",
            sections: [{ id: "goals", title: "Goals", body_md: "Private.", ordinal: 0 }],
          }),
        })
      ).status,
    ).toBe(200);
    expect(
      (
        await bob.app.request(`/v1/projects/${bobProject.id}/context/nodes/${bobNode}`, {
          method: "PUT",
          headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
          body: JSON.stringify({
            scope_type: "project",
            sections: [{ id: "goals", title: "Goals", body_md: "Bob.", ordinal: 0 }],
          }),
        })
      ).status,
    ).toBe(200);

    const outsider = await bob.app.request(
      `/v1/projects/${aliceProject.id}/context/nodes/${aliceNode}`,
      {
        method: "PUT",
        headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
        body: JSON.stringify({ review_state: "reviewed" }),
      },
    );
    expect(outsider.status).toBe(404);
    expect(await outsider.json()).toMatchObject({ error: { code: "not_found" } });

    const crossProject = await alice.app.request(
      `/v1/projects/${aliceProject.id}/context/nodes/${bobNode}`,
      {
        method: "PUT",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({ review_state: "reviewed" }),
      },
    );
    expect(crossProject.status).toBe(404);
    expect(await crossProject.json()).toMatchObject({ error: { code: "not_found" } });
  });

  it("returns 404 when an unknown id is updated without create fields", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "missing-update");
    const res = await alice.app.request(`/v1/projects/${project.id}/context/nodes/${uuidv7()}`, {
      method: "PUT",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ review_state: "reviewed" }),
    });
    expect(res.status).toBe(404);
    expect(await res.json()).toMatchObject({
      error: { code: "not_found", message: "node not found" },
    });
  });

  it("returns 404 for an unknown or foreign repo_id on native create", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const aliceProject = await createProject(alice.app, alice.token, "alice-repo");
    const bobProject = await createProject(bob.app, bob.token, "bob-repo");
    const foreignRepo = uuidv7();
    store.seedProjectRepo({
      id: foreignRepo,
      projectId: bobProject.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: "/tmp/beacon",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });

    const unknown = await alice.app.request(
      `/v1/projects/${aliceProject.id}/context/nodes/${uuidv7()}`,
      {
        method: "PUT",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          scope_type: "repo",
          repo_id: uuidv7(),
          sections: [{ id: "goals", title: "Goals", body_md: "Nope.", ordinal: 0 }],
        }),
      },
    );
    expect(unknown.status).toBe(404);
    expect(await unknown.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });

    const foreign = await alice.app.request(
      `/v1/projects/${aliceProject.id}/context/nodes/${uuidv7()}`,
      {
        method: "PUT",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          scope_type: "repo",
          repo_id: foreignRepo,
          sections: [{ id: "goals", title: "Goals", body_md: "Nope.", ordinal: 0 }],
        }),
      },
    );
    expect(foreign.status).toBe(404);
    expect(await foreign.json()).toMatchObject({
      error: { code: "not_found", message: "repo not found" },
    });
  });

  it("returns 404 when creating onto an existing project scope with a new id", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "same-scope");
    const first = uuidv7();
    const created = await alice.app.request(`/v1/projects/${project.id}/context/nodes/${first}`, {
      method: "PUT",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        scope_type: "project",
        sections: [{ id: "goals", title: "Goals", body_md: "First.", ordinal: 0 }],
      }),
    });
    expect(created.status).toBe(200);

    const raced = await alice.app.request(`/v1/projects/${project.id}/context/nodes/${uuidv7()}`, {
      method: "PUT",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        scope_type: "project",
        sections: [{ id: "goals", title: "Goals", body_md: "Second.", ordinal: 0 }],
      }),
    });
    expect(raced.status).toBe(404);
    expect(await raced.json()).toMatchObject({
      error: { code: "not_found", message: "node not found" },
    });
    const stored = await store.listContextNodes(project.id);
    expect(stored).toHaveLength(1);
    expect(stored[0]?.id).toBe(first);
    expect(stored[0]?.sections[0]).toMatchObject({ body_md: "First." });
  });
});

describe("POST /v1/projects/:id/context/nodes", () => {
  it("mints a node id and defaults missing scope_type to project", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "post-node");

    const created = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        sections: [{ id: "goals", title: "Goals", body_md: "Ship the brief.", ordinal: 0 }],
      }),
    });
    expect(created.status).toBe(200);
    const createdBody = (await created.json()) as {
      id: string;
      source: string;
      scope_type: string;
      review_state: string;
      sections: { id: string; body_md: string }[];
    };
    expect(isUuidV7(createdBody.id)).toBe(true);
    expect(createdBody).toMatchObject({
      source: "native",
      scope_type: "project",
      review_state: "reviewed",
    });
    expect(createdBody.sections[0]).toMatchObject({ id: "goals", body_md: "Ship the brief." });

    const listed = await store.listContextNodes(project.id);
    expect(listed).toHaveLength(1);
    expect(listed[0]?.id).toBe(createdBody.id);
  });

  it("creates a repo-scoped node when scope_type is sent", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "post-repo");
    const repoId = uuidv7();
    store.seedProjectRepo({
      id: repoId,
      projectId: project.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: "/tmp/beacon",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });

    const created = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        scope_type: "repo",
        path: "",
        repo_id: repoId,
        sections: [{ id: "stack", title: "Tech stack", body_md: "TypeScript", ordinal: 0 }],
      }),
    });
    expect(created.status).toBe(200);
    expect(await created.json()).toMatchObject({
      scope_type: "repo",
      repo_id: repoId,
      source: "native",
      sections: [{ id: "stack", body_md: "TypeScript" }],
    });
  });

  it("returns 404 when creating onto an existing project scope", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "post-dup");
    const first = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        sections: [{ id: "goals", title: "Goals", body_md: "First.", ordinal: 0 }],
      }),
    });
    expect(first.status).toBe(200);
    const firstBody = (await first.json()) as { id: string };

    const raced = await alice.app.request(`/v1/projects/${project.id}/context/nodes`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        sections: [{ id: "goals", title: "Goals", body_md: "Second.", ordinal: 0 }],
      }),
    });
    expect(raced.status).toBe(404);
    expect(await raced.json()).toMatchObject({
      error: { code: "not_found", message: "node not found" },
    });
    const stored = await store.listContextNodes(project.id);
    expect(stored).toHaveLength(1);
    expect(stored[0]?.id).toBe(firstBody.id);
    expect(stored[0]?.sections[0]).toMatchObject({ body_md: "First." });
  });
});

describe("GET /v1/projects/:id/context/revisions/:revId", () => {
  it("returns a stored compile revision for a project member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "revs");

    const compiled = await alice.app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(compiled.status).toBe(200);
    const brief = (await compiled.json()) as { revision_id: string; sections: unknown[] };

    const listed = await alice.app.request(`/v1/projects/${project.id}/context/revisions`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    const page = (await listed.json()) as { items: { id: string }[] };
    expect(page.items[0]?.id).toBe(brief.revision_id);

    const res = await alice.app.request(
      `/v1/projects/${project.id}/context/revisions/${brief.revision_id}`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    expect(res.status).toBe(200);
    const revision = (await res.json()) as {
      id: string;
      brief: { revision_id: string };
      brief_markdown: string;
    };
    expect(revision.id).toBe(brief.revision_id);
    expect(revision.brief.revision_id).toBe(brief.revision_id);
    expect(revision.brief_markdown.length).toBeGreaterThan(0);
  });

  it("returns 404 for a non-member or foreign revision", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const aliceProject = await createProject(alice.app, alice.token, "alice-rev");
    const bobProject = await createProject(bob.app, bob.token, "bob-rev");

    const compiled = await alice.app.request(`/v1/projects/${aliceProject.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    const brief = (await compiled.json()) as { revision_id: string };

    const outsider = await bob.app.request(
      `/v1/projects/${aliceProject.id}/context/revisions/${brief.revision_id}`,
      { headers: { cookie: cookieHeader(bob.token) } },
    );
    expect(outsider.status).toBe(404);
    expect(await outsider.json()).toMatchObject({ error: { code: "not_found" } });

    const foreign = await bob.app.request(
      `/v1/projects/${bobProject.id}/context/revisions/${brief.revision_id}`,
      { headers: { cookie: cookieHeader(bob.token) } },
    );
    expect(foreign.status).toBe(404);
    expect(await foreign.json()).toMatchObject({ error: { code: "not_found" } });
  });
});

describe("POST /v1/projects/:id/context/import", () => {
  it("parses files, upserts nodes as needs_review, and writes CODEOWNERS", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "import");
    const repoId = uuidv7();
    store.seedProjectRepo({
      id: repoId,
      projectId: project.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: "/tmp/beacon",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });

    const res = await alice.app.request(
      `/v1/projects/${project.id}/context/import?repo_id=${repoId}`,
      {
        method: "POST",
        headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
        body: JSON.stringify({
          files: [
            {
              path: "AGENTS.md",
              content: "## Goals\nShip the compiler.\n\n## Security\nNo tokens.\n",
            },
            { path: "CONVENTIONS.md", content: "## Conventions\nMatch existing routes.\n" },
            {
              path: "CODEOWNERS",
              content: "* @beacon/core\napps/api/ @beacon/api\n",
            },
          ],
        }),
      },
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      nodes: { review_state: string; source: string }[];
      code_owners_written: number;
    };
    expect(body.nodes.length).toBeGreaterThan(0);
    expect(body.nodes.every((node) => node.review_state === "needs_review")).toBe(true);
    expect(body.code_owners_written).toBe(2);
    expect(await store.listCodeOwners(repoId)).toHaveLength(2);
  });

  it("writes nodes and code owners through one importContext call", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-01-01T00:00:00.000Z");
    const projectId = uuidv7();
    const repoId = uuidv7();
    const imported = await store.importContext({
      nodes: [
        {
          id: uuidv7(now.getTime()),
          projectId,
          repoId,
          taskId: null,
          scopeType: "repo",
          path: "",
          sections: [{ id: "goals", title: "Goals", body_md: "Ship it.", ordinal: 0 }],
          sectionsText: "Goals\nShip it.",
          source: "agents.md",
          sourcePath: "AGENTS.md",
          reviewState: "needs_review",
          updatedByType: "user",
          updatedById: "user-1",
          updatedAt: now,
        },
      ],
      codeOwners: {
        repoId,
        rows: [
          {
            id: uuidv7(now.getTime() + 1),
            repoId,
            pathPattern: "*",
            owners: ["@beacon/core"],
            source: "codeowners",
          },
        ],
      },
    });
    expect(imported.nodes).toHaveLength(1);
    expect(imported.codeOwnersWritten).toBe(1);
    expect(await store.listContextNodes(projectId)).toHaveLength(1);
    expect(await store.listCodeOwners(repoId)).toHaveLength(1);
  });

  it("returns 404 for a non-member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret-import");
    const res = await bob.app.request(`/v1/projects/${project.id}/context/import`, {
      method: "POST",
      headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
      body: JSON.stringify({ files: [] }),
    });
    expect(res.status).toBe(404);
  });
});

describe("GET /v1/projects/:id/context/export/agents-md", () => {
  it("returns a managed-by projection for the chosen scope", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "export");
    await alice.app.request(`/v1/projects/${project.id}/context/import`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        files: [{ path: "AGENTS.md", content: "## Goals\nShip it.\n" }],
      }),
    });

    const res = await alice.app.request(`/v1/projects/${project.id}/context/export/agents-md`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toMatch(/text\/markdown/);
    const markdown = await res.text();
    expect(markdown).toContain("managed-by: projectbeacon");
    expect(markdown).toContain("scope: project");
    expect(markdown).toContain("## Goals");
    expect(markdown).toContain("Ship it.");
  });

  it("defaults to the sole repo the same way import does", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "export-repo");
    const repoId = uuidv7();
    store.seedProjectRepo({
      id: repoId,
      projectId: project.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: "/tmp/beacon",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });
    await alice.app.request(`/v1/projects/${project.id}/context/import`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        files: [{ path: "AGENTS.md", content: "## Goals\nShip it.\n" }],
      }),
    });

    const res = await alice.app.request(`/v1/projects/${project.id}/context/export/agents-md`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(res.status).toBe(200);
    const markdown = await res.text();
    expect(markdown).toContain("managed-by: projectbeacon");
    expect(markdown).toContain(`scope: ${repoId}`);
    expect(markdown).toContain("## Goals");
    expect(markdown).toContain("Ship it.");
  });
});

describe("POST /v1/projects/:id/context/compile", () => {
  it("returns 200 for a project member and writes a revision", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "brief");

    const res = await alice.app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(res.status).toBe(200);
    const brief = (await res.json()) as {
      schema_version: string;
      budget: { dropped: string[]; overflow: boolean };
      changed_scope: unknown;
      tree_capsule: unknown;
      revision_id: string;
    };
    expect(brief.schema_version).toBe("1");
    expect(brief.changed_scope).toBeNull();
    expect(brief.tree_capsule).toBeNull();
    expect(brief.budget.dropped).toEqual(expect.arrayContaining(["changed_scope", "tree_capsule"]));
    expect(brief.budget.overflow).toBe(false);

    const revisions = await store.listContextRevisions(project.id);
    expect(revisions).toHaveLength(1);
    expect(revisions[0]?.id).toBe(brief.revision_id);
  });

  it("attaches extras without calling CodeGateway", async () => {
    const queries: unknown[] = [];
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "extras");
    const repoId = uuidv7();
    store.seedProjectRepo({
      id: repoId,
      projectId: project.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: "/tmp/beacon",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });
    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      codeGateway: {
        async query(_repo, query) {
          queries.push(query);
          return { items: [] };
        },
        async health() {
          return true;
        },
      },
    });
    const extras = {
      tree_capsule: {
        repo_id: repoId,
        root: ".",
        entries: [{ path: "apps", kind: "dir" }],
      },
    };
    const res = await app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ extras }),
    });
    expect(res.status).toBe(200);
    expect(queries).toEqual([]);
    expect(await res.json()).toMatchObject({ tree_capsule: extras.tree_capsule });
  });

  it("returns 404 for a non-member", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret");

    const res = await bob.app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(res.status).toBe(404);
    expect(await res.json()).toMatchObject({ error: { code: "not_found" } });
    expect(await store.listContextRevisions(project.id)).toHaveLength(0);
  });

  it("lets a project token compile and search context", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "token-ctx");
    const minted = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "mcp" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;
    store.seedContextNode({
      id: "018f1e2c-3d4e-7000-8000-0000000000c1",
      projectId: project.id,
      repoId: null,
      taskId: null,
      scopeType: "project",
      path: "AGENTS.md",
      sections: [{ id: "goals", title: "Goals", body_md: "Ship the control plane", ordinal: 0 }],
      sectionsText: "Goals\nShip the control plane",
      source: "manual",
      sourcePath: null,
      reviewState: "reviewed",
      updatedByType: "user",
      updatedById: alice.user.id,
      updatedAt: new Date("2026-01-01T00:00:00.000Z"),
    });

    const compiled = await alice.app.request(`/v1/projects/${project.id}/context/compile`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(compiled.status).toBe(200);

    const search = await alice.app.request(`/v1/projects/${project.id}/context/search?q=control`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(search.status).toBe(200);
    expect(await search.json()).toMatchObject({
      items: [expect.objectContaining({ path: "AGENTS.md" })],
      next_cursor: null,
    });
  });
});
