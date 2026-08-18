import { describe, expect, it } from "vitest";

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
