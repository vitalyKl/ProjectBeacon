import { describe, expect, it } from "vitest";

import { uuidv7 } from "@beacon/shared";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";

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
            { path: "AGENTS.md", content: "## Goals\nShip the compiler.\n\n## Security\nNo tokens.\n" },
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
});
