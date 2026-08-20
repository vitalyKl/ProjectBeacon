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
    workerToken: undefined,
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

  it("keeps the owned personal org after joining another user's older personal-kind membership", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const aliceUser = (await store.findUserById(alice.user.id))!;
    const bobUser = (await store.findUserById(bob.user.id))!;
    const aliceOrg = await store.ensurePersonalOrg(aliceUser, new Date("2026-01-01T00:00:00.000Z"));
    const bobOrg = await store.ensurePersonalOrg(bobUser, new Date("2026-01-02T00:00:00.000Z"));

    await store.upsertOrgMember({ orgId: aliceOrg.id, userId: bob.user.id, role: "member" });

    const me = await bob.app.request("/v1/me", { headers: { cookie: cookieHeader(bob.token!) } });
    const body = (await me.json()) as { personal_org: { id: string; slug: string } };
    expect(body.personal_org.id).toBe(bobOrg.id);
    expect(body.personal_org.id).not.toBe(aliceOrg.id);
    expect((await store.findOrgMember(bobOrg.id, bob.user.id))?.role).toBe("owner");
  });

  it("suffixes the personal org slug when the login slug is already taken", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    await alice.app.request("/v1/orgs", {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "bob", name: "Taken" }),
    });
    const bob = await registerUser(store, "bob");
    const personal = await store.ensurePersonalOrg(
      (await store.findUserById(bob.user.id))!,
      new Date("2026-01-01T00:00:00.000Z"),
    );
    expect(personal.slug).toBe("bob-2");
    expect(personal.id).toBe(bob.user.id);
    expect((await store.findOrgMember(personal.id, bob.user.id))?.role).toBe("owner");
  });

  it("does not create a second personal org on later login", async () => {
    const store = new MemoryAuthStore();
    const { app } = await bootstrapAdmin(store);
    const login = await app.request("/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    expect(login.status).toBe(200);
    const admin = (await store.findUserByLogin("admin"))!;
    const orgs = (await store.listOrgsForUser(admin.id)).filter((org) => org.kind === "personal");
    expect(orgs).toHaveLength(1);
    expect(orgs[0]?.id).toBe(admin.id);
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

  it("patches project name and settings in one write", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "settings", name: "Settings" }),
    });
    const project = (await created.json()) as { id: string };
    const patched = await app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({
        name: "Beacon settings",
        settings: { github: { issues: "import" } },
      }),
    });
    expect(patched.status).toBe(200);
    expect(await patched.json()).toMatchObject({
      name: "Beacon settings",
      settings: { github: { issues: "import" } },
    });
    const stored = await store.findProjectById(project.id);
    expect(stored?.name).toBe("Beacon settings");
    expect(stored?.settings).toEqual({ github: { issues: "import" } });
  });

  it("lets a project token read GET /v1/projects/:id", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "agent", name: "Agent" }),
    });
    const project = (await created.json()) as { id: string; name: string };
    const minted = await app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ name: "cli" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const res = await app.request(`/v1/projects/${project.id}`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ id: project.id, name: "Agent" });
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
    expect(await res.json()).toMatchObject({ error: { code: "invalid_request" } });
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

  it("preserves a higher project role when accepting a lower invite", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const alice = await registerUser(store, "alice");
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "keep-admin", name: "Keep admin" }),
    });
    const project = (await created.json()) as { id: string };

    await store.upsertProjectMember({
      projectId: project.id,
      userId: alice.user.id,
      role: "admin",
      createdAt: new Date("2026-01-01T00:00:00.000Z"),
    });

    const inviteRes = await app.request(`/v1/projects/${project.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "alice", role: "read" }),
    });
    const invite = (await inviteRes.json()) as { id: string };
    const accept = await app.request(`/v1/project-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token!) },
    });
    expect(accept.status).toBe(200);
    expect(await store.findProjectMember(project.id, alice.user.id)).toMatchObject({
      role: "admin",
    });
  });

  it("lets a project admin set a lower role via POST /members", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const alice = await registerUser(store, "alice");
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "set-role", name: "Set role" }),
    });
    const project = (await created.json()) as { id: string };

    const add = await app.request(`/v1/projects/${project.id}/members`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ user_id: alice.user.id, role: "admin" }),
    });
    expect(add.status).toBe(201);
    expect(await store.findProjectMember(project.id, alice.user.id)).toMatchObject({
      role: "admin",
    });

    const demote = await app.request(`/v1/projects/${project.id}/members`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ user_id: alice.user.id, role: "read" }),
    });
    expect(demote.status).toBe(201);
    expect(await demote.json()).toMatchObject({ user_id: alice.user.id, role: "read" });
    expect(await store.findProjectMember(project.id, alice.user.id)).toMatchObject({
      role: "read",
    });
  });

  it("preserves org owner when accepting a lower org invite", async () => {
    const store = new MemoryAuthStore();
    const owner = await registerUser(store, "owner");
    const admin = await registerUser(store, "admin-user");

    const orgRes = await owner.app.request("/v1/orgs", {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "keep-owner", name: "Keep owner" }),
    });
    const org = (await orgRes.json()) as { id: string };

    await store.upsertOrgMember({ orgId: org.id, userId: admin.user.id, role: "admin" });
    const inviteRes = await owner.app.request(`/v1/orgs/${org.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "owner", role: "member" }),
    });
    const invite = (await inviteRes.json()) as { id: string };
    const accept = await owner.app.request(`/v1/org-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(owner.token!) },
    });
    expect(accept.status).toBe(200);
    expect(await store.findOrgMember(org.id, owner.user.id)).toMatchObject({ role: "owner" });
  });

  it("rejects expired and already-accepted project invites", async () => {
    const store = new MemoryAuthStore();
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const config = testConfig();
    const app = createApp({ store, config, clock, checkReady: async () => true });
    const boot = await app.request("/v1/auth/bootstrap", {
      method: "POST",
      headers: {
        authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(boot);
    const aliceApp = createApp({ store, config, clock, checkReady: async () => true });
    const aliceRes = await aliceApp.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "alice", password: STRONG_PASSWORD }),
    });
    const aliceToken = sessionCookie(aliceRes);

    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "expiry", name: "Expiry" }),
    });
    const project = (await created.json()) as { id: string };
    const inviteRes = await app.request(`/v1/projects/${project.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "alice", role: "read" }),
    });
    const invite = (await inviteRes.json()) as { id: string };

    const first = await app.request(`/v1/project-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(aliceToken!) },
    });
    expect(first.status).toBe(200);
    const second = await app.request(`/v1/project-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(aliceToken!) },
    });
    expect(second.status).toBe(404);

    const expiredInvite = await app.request(`/v1/projects/${project.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "alice", role: "write" }),
    });
    const later = (await expiredInvite.json()) as { id: string };
    now = new Date("2026-01-09T00:00:00.000Z");
    const expired = await app.request(`/v1/project-invites/${later.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(aliceToken!) },
    });
    expect(expired.status).toBe(404);
    expect(
      (await store.findProjectMember(project.id, (await store.findUserByLogin("alice"))!.id))?.role,
    ).toBe("read");
  });

  it("does not accept an invite for a soft-deleted project", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const alice = await registerUser(store, "alice");
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "gone", name: "Gone" }),
    });
    const project = (await created.json()) as { id: string };
    const inviteRes = await app.request(`/v1/projects/${project.id}/invites`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ github_login: "alice", role: "read" }),
    });
    const invite = (await inviteRes.json()) as { id: string };
    const del = await app.request(`/v1/projects/${project.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(token!) },
    });
    expect(del.status).toBe(200);

    const accept = await app.request(`/v1/project-invites/${invite.id}/accept`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token!) },
    });
    expect(accept.status).toBe(404);
    expect(await store.findProjectMember(project.id, alice.user.id)).toBeUndefined();
  });

  it("treats mixed-case org slugs as the stored lowercase slug", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    await app.request("/v1/orgs", {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "acme", name: "Acme" }),
    });
    const listed = await app.request("/v1/orgs/Acme/members", {
      headers: { cookie: cookieHeader(token!) },
    });
    expect(listed.status).toBe(200);
  });

  it("conflicts on a slug reused after soft-delete", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "reuse", name: "Reuse" }),
    });
    const project = (await created.json()) as { id: string };
    await app.request(`/v1/projects/${project.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(token!) },
    });
    const again = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "reuse", name: "Reuse again" }),
    });
    expect(again.status).toBe(409);
    expect(await again.json()).toMatchObject({
      error: { code: "login_taken", details: { field: "slug" } },
    });
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

  it("returns 404 for GET/PATCH/DELETE of a soft-deleted project even for a former admin", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "tombstone", name: "Tombstone" }),
    });
    const project = (await created.json()) as { id: string };
    const del = await app.request(`/v1/projects/${project.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(token!) },
    });
    expect(del.status).toBe(200);

    const get = await app.request(`/v1/projects/${project.id}`, {
      headers: { cookie: cookieHeader(token!) },
    });
    expect(get.status).toBe(404);
    const patch = await app.request(`/v1/projects/${project.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ name: "Still gone" }),
    });
    expect(patch.status).toBe(404);
    const again = await app.request(`/v1/projects/${project.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(token!) },
    });
    expect(again.status).toBe(404);
  });

  it("lets a project token GET the project and hides other projects", async () => {
    const store = new MemoryAuthStore();
    const { app, token } = await bootstrapAdmin(store);
    const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token!) } });
    const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
    const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "token-get", name: "Token get" }),
    });
    const project = (await created.json()) as { id: string; name: string };
    const other = await app.request(`/v1/orgs/${personal.id}/projects`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ slug: "other", name: "Other" }),
    });
    const otherProject = (await other.json()) as { id: string };
    const minted = await app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(token!), "content-type": "application/json" },
      body: JSON.stringify({ name: "mcp" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const got = await app.request(`/v1/projects/${project.id}`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(got.status).toBe(200);
    expect(await got.json()).toMatchObject({ id: project.id, name: "Token get" });

    const hidden = await app.request(`/v1/projects/${otherProject.id}`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(hidden.status).toBe(404);
    expect(await hidden.json()).toMatchObject({
      error: { code: "not_found", message: "project not found" },
    });
  });
});
