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

async function registerUser(
  store: MemoryAuthStore,
  login: string,
  config: AuthConfig = testConfig(),
) {
  const app = createApp({ store, config, checkReady: async () => true });
  const res = await app.request("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ login, password: STRONG_PASSWORD }),
  });
  const token = sessionCookie(res);
  const body = (await res.json()) as { id: string; login: string };
  return { app, res, token, user: body };
}

async function bootstrapAdmin(store: MemoryAuthStore, config: AuthConfig = testConfig()) {
  const app = createApp({ store, config, checkReady: async () => true });
  const res = await app.request("/v1/auth/bootstrap", {
    method: "POST",
    headers: {
      authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
  });
  return { app, res, token: sessionCookie(res), body: await res.json() };
}

describe("personal org on first login", () => {
  it("creates a personal org owned by the bootstrap user", async () => {
    const store = new MemoryAuthStore();
    const { token, body } = await bootstrapAdmin(store);
    expect(token).toBeDefined();

    const admin = body as { id: string; login: string };
    const personal = await store.ensurePersonalOrg(
      (await store.findUserById(admin.id))!,
      new Date("2026-01-01T00:00:00.000Z"),
    );
    expect(personal.kind).toBe("personal");
    expect(personal.slug).toBe("admin");
    expect(await store.findOrgMember(personal.id, admin.id)).toMatchObject({ role: "owner" });
  });

  it("includes personal org and orgs list on GET /v1/me", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);

    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    expect(me.status).toBe(200);
    const body = (await me.json()) as {
      login: string;
      personal_org: { slug: string; kind: string };
      orgs: { slug: string; kind: string }[];
    };
    expect(body.personal_org).toMatchObject({ slug: "admin", kind: "personal" });
    expect(body.orgs).toEqual(expect.arrayContaining([expect.objectContaining({ slug: "admin" })]));
  });
});

describe("orgs and projects", () => {
  it("lets any authenticated user create a team org and a private project as admin", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);

    const orgRes = await app.request("/v1/orgs", {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "acme", name: "Acme" }),
    });
    expect(orgRes.status).toBe(201);
    const org = (await orgRes.json()) as { id: string; slug: string; kind: string };
    expect(org).toMatchObject({ slug: "acme", kind: "team" });

    const projectRes = await app.request(`/v1/orgs/${org.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "beacon", name: "Beacon" }),
    });
    expect(projectRes.status).toBe(201);
    const project = (await projectRes.json()) as { id: string; visibility: string; org_id: string };
    expect(project.visibility).toBe("private");
    expect(project.org_id).toBe(org.id);

    const members = await app.request(`/v1/projects/${project.id}/members`, {
      headers: { cookie: cookieHeader(token!) },
    });
    expect(members.status).toBe(200);
    const listed = (await members.json()) as { items: { role: string }[] };
    expect(listed.items).toEqual([expect.objectContaining({ role: "admin" })]);
  });

  it("rejects non-private visibility", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;

    const res = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "open", name: "Open", visibility: "internal" }),
    });
    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: { code: "unauthorized" } });
  });

  it("accepts a project invite and upserts project_members", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const alice = await registerUser(store, "alice");

    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "shared", name: "Shared" }),
    });
    const project = (await created.json()) as { id: string };

    const inviteRes = await app.request(`/v1/projects/${project.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "alice", role: "write" }),
    });
    expect(inviteRes.status).toBe(201);
    const invite = (await inviteRes.json()) as { id: string };

    const accept = await app.request(`/v1/project-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token!) },
    });
    expect(accept.status).toBe(200);

    const asAlice = await app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(alice.token!) },
    });
    expect(asAlice.status).toBe(200);
  });
});

describe("IDOR", () => {
  it("prevents user A from GET/PATCH/DELETE user B's project", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");

    const aliceMe = await alice.app.request("/v1/me", {
      headers: { cookie: cookieHeader(alice.token!) },
    });
    const aliceOrg = ((await aliceMe.json()) as { personal_org: { id: string } }).personal_org;
    const created = await alice.app.request(`/v1/orgs/${aliceOrg.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "secret", name: "Secret" }),
    });
    const project = (await created.json()) as { id: string };

    const get = await bob.app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(bob.token!) },
    });
    expect(get.status).toBe(404);
    expect(await get.json()).toMatchObject({ error: { code: "not_found" } });

    const patch = await bob.app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(bob.token!), "content-type": "application/json" },
      body: JSON.stringify({ name: "Hacked" }),
    });
    expect(patch.status).toBe(404);

    const del = await bob.app.request(`/v1/projects/${project.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(bob.token!) },
    });
    expect(del.status).toBe(404);

    const still = await alice.app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(alice.token!) },
    });
    expect(still.status).toBe(200);
    expect(await still.json()).toMatchObject({ name: "Secret" });
  });

  it("does not grant project access from org membership alone", async () => {
    const store = new MemoryAuthStore();
    const owner = await registerUser(store, "owner");
    const member = await registerUser(store, "member");

    const orgRes = await owner.app.request("/v1/orgs", {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "team", name: "Team" }),
    });
    const org = (await orgRes.json()) as { id: string };

    const inviteRes = await owner.app.request(`/v1/orgs/${org.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "member", role: "member" }),
    });
    const invite = (await inviteRes.json()) as { id: string };
    const accepted = await member.app.request(`/v1/org-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(member.token!) },
    });
    expect(accepted.status).toBe(200);

    const projectRes = await owner.app.request(`/v1/orgs/${org.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "only-owner", name: "Only owner" }),
    });
    const project = (await projectRes.json()) as { id: string };

    const listed = await member.app.request(`/v1/orgs/${org.id}/projects`, {
      headers: { cookie: cookieHeader(member.token!) },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toEqual({ items: [], next_cursor: null });

    const get = await member.app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(member.token!) },
    });
    expect(get.status).toBe(404);
    expect(await get.json()).toMatchObject({ error: { code: "not_found" } });
  });
});
