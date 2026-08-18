import { uuidv7 } from "@beacon/shared";
import { describe, expect, it } from "vitest";

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
  return (await minted.json()) as { token: string; scopes: string[] };
}

describe("decisions and constraints", () => {
  it("creates, lists, and repeats a decision with the same Idempotency-Key", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "adr");

    const created = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "adr-1",
      },
      body: JSON.stringify({
        title: "Use Zod",
        context: "Need a contract",
        decision: "Generate OpenAPI from Zod.",
        consequences: "Spec stays in lockstep.",
        status: "accepted",
      }),
    });
    expect(created.status).toBe(201);
    const decision = (await created.json()) as {
      id: string;
      status: string;
      decision: string;
      related_paths: unknown[];
      related_task_ids: unknown[];
    };
    expect(decision).toMatchObject({
      title: "Use Zod",
      status: "accepted",
      decision: "Generate OpenAPI from Zod.",
      consequences: "Spec stays in lockstep.",
      project_id: project.id,
    });
    expect(decision.related_paths).toEqual([]);
    expect(decision.related_task_ids).toEqual([]);

    const again = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "adr-1",
      },
      body: JSON.stringify({
        title: "Use Zod",
        context: "Need a contract",
        decision: "Generate OpenAPI from Zod.",
      }),
    });
    expect(again.status).toBe(201);
    expect(((await again.json()) as { id: string }).id).toBe(decision.id);

    const listed = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toMatchObject({
      items: [expect.objectContaining({ id: decision.id, status: "accepted" })],
      next_cursor: null,
    });
  });

  it("forces agent record_decision and create_constraint to proposed", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "policy");
    const minted = await mintToken(alice.app, alice.token, project.id);

    const decision = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
        "idempotency-key": "agent-adr",
      },
      body: JSON.stringify({
        title: "Ship MCP",
        context: "Agents need tools",
        decision: "HTTP first",
        status: "accepted",
      }),
    });
    expect(decision.status).toBe(201);
    expect(await decision.json()).toMatchObject({ status: "proposed" });

    const constraint = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
        "idempotency-key": "agent-rule",
      },
      body: JSON.stringify({
        kind: "must",
        body: "Do not leak secrets",
        status: "active",
      }),
    });
    expect(constraint.status).toBe(201);
    expect(await constraint.json()).toMatchObject({ status: "proposed" });
  });

  it("lets a human create an active constraint and apply a proposed one", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "rules");

    const active = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "human-active",
      },
      body: JSON.stringify({
        kind: "security",
        body: "No secrets in logs",
        status: "active",
      }),
    });
    expect(active.status).toBe(201);
    expect(await active.json()).toMatchObject({ status: "active", kind: "security" });

    const proposed = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "human-proposed",
      },
      body: JSON.stringify({ kind: "must", body: "Keep ADRs short" }),
    });
    expect(proposed.status).toBe(201);
    const row = (await proposed.json()) as { id: string; status: string };
    expect(row.status).toBe("proposed");

    const applied = await alice.app.request(`/v1/constraints/${row.id}/apply`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: "{}",
    });
    expect(applied.status).toBe(200);
    expect(await applied.json()).toMatchObject({ id: row.id, status: "active" });
  });

  it("denies agent apply and creates an approval when asked", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "gate");
    const minted = await mintToken(alice.app, alice.token, project.id);

    const created = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
        "idempotency-key": "to-apply",
      },
      body: JSON.stringify({ kind: "must_not", body: "Do not skip review" }),
    });
    const constraint = (await created.json()) as { id: string };

    const denied = await alice.app.request(`/v1/constraints/${constraint.id}/apply`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
      },
      body: "{}",
    });
    expect(denied.status).toBe(403);
    expect(await denied.json()).toMatchObject({ error: { code: "forbidden" } });
    expect(await store.listApprovals(project.id)).toHaveLength(0);

    const requested = await alice.app.request(`/v1/constraints/${constraint.id}/apply`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${minted.token}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({ request_approval: true }),
    });
    expect(requested.status).toBe(403);
    const body = (await requested.json()) as {
      error: { details: { approval: { id: string; action: string; status: string } } };
    };
    expect(body.error.details.approval).toMatchObject({
      action: "constraints.apply",
      status: "pending",
    });
    expect(await store.listApprovals(project.id, "pending")).toHaveLength(1);
    expect((await store.findConstraintById(constraint.id))?.status).toBe("proposed");
  });

  it("returns 400 for unknown related task or repo ids", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "fks");

    const missingTask = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "bad-task",
      },
      body: JSON.stringify({
        title: "Link missing task",
        context: "Need a task",
        decision: "Reject unknown ids",
        related_task_ids: [uuidv7()],
      }),
    });
    expect(missingTask.status).toBe(400);
    expect(await missingTask.json()).toMatchObject({
      error: { details: { reason: "invalid_related_task" } },
    });

    const missingRepo = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "bad-path",
      },
      body: JSON.stringify({
        title: "Link missing repo",
        context: "Need a path",
        decision: "Reject unknown repos",
        related_paths: [{ repo_id: uuidv7(), path: "apps/api" }],
      }),
    });
    expect(missingRepo.status).toBe(400);
    expect(await missingRepo.json()).toMatchObject({
      error: { details: { reason: "invalid_related_path" } },
    });
  });

  it("returns 404 for non-members", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret");

    const listed = await bob.app.request(`/v1/projects/${project.id}/decisions`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(listed.status).toBe(404);

    const created = await bob.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(bob.token),
        "content-type": "application/json",
        "idempotency-key": "idor",
      },
      body: JSON.stringify({ kind: "must", body: "nope" }),
    });
    expect(created.status).toBe(404);

    const constraint = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "visible",
      },
      body: JSON.stringify({ kind: "must", body: "members only" }),
    });
    const row = (await constraint.json()) as { id: string };
    const applied = await bob.app.request(`/v1/constraints/${row.id}/apply`, {
      method: "POST",
      headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
      body: "{}",
    });
    expect(applied.status).toBe(404);
  });

  it("persists related tasks and paths when the FKs belong to the project", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "links");
    const repoId = uuidv7();
    store.seedProjectRepo({
      id: repoId,
      projectId: project.id,
      provider: "local",
      remoteUrl: null,
      defaultBranch: "main",
      githubRepoId: null,
      installationId: null,
      localRootHint: ".",
      indexMode: "sidecar",
      lastIndexedSha: null,
      lastIndexedAt: null,
    });

    const taskRes = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "linked-task",
      },
      body: JSON.stringify({ title: "Ship ADR" }),
    });
    const task = (await taskRes.json()) as { id: string };

    const created = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "linked-adr",
      },
      body: JSON.stringify({
        title: "Keep join tables",
        context: "Need related rows",
        decision: "Validate FKs first",
        related_task_ids: [task.id],
        related_paths: [{ repo_id: repoId, path: "apps/api" }],
      }),
    });
    expect(created.status).toBe(201);
    expect(await created.json()).toMatchObject({
      related_task_ids: [task.id],
      related_paths: [{ repo_id: repoId, path: "apps/api" }],
    });
  });

  it("denies apply for a write member without constraints:apply", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const writer = await registerUser(store, "writer");
    const project = await createProject(alice.app, alice.token, "write-role");
    await alice.app.request(`/v1/projects/${project.id}/members`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ user_id: writer.user.id, role: "write" }),
    });

    const created = await writer.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(writer.token),
        "content-type": "application/json",
        "idempotency-key": "write-create",
      },
      body: JSON.stringify({ kind: "must", body: "Need apply later" }),
    });
    const row = (await created.json()) as { id: string };

    const applied = await writer.app.request(`/v1/constraints/${row.id}/apply`, {
      method: "POST",
      headers: { cookie: cookieHeader(writer.token), "content-type": "application/json" },
      body: "{}",
    });
    expect(applied.status).toBe(403);
    expect((await store.findConstraintById(row.id))?.status).toBe("proposed");
  });

  it("requires Idempotency-Key on decision and constraint create", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "need-key");

    const decision = await alice.app.request(`/v1/projects/${project.id}/decisions`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        title: "No key",
        context: "Missing header",
        decision: "Reject",
      }),
    });
    expect(decision.status).toBe(400);
    expect(await decision.json()).toMatchObject({
      error: { details: { reason: "missing_idempotency_key" } },
    });

    const constraint = await alice.app.request(`/v1/projects/${project.id}/constraints`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ kind: "must", body: "No key" }),
    });
    expect(constraint.status).toBe(400);
    expect(await constraint.json()).toMatchObject({
      error: { details: { reason: "missing_idempotency_key" } },
    });
  });
});
