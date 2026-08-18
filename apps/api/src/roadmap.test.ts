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
    workerToken: undefined,
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

type TaskBody = {
  id: string;
  title: string;
  status: string;
  type: string;
  version: number;
  project_id: string;
};

async function mintToken(app: ReturnType<typeof createApp>, cookie: string, projectId: string) {
  const minted = await app.request(`/v1/projects/${projectId}/tokens`, {
    method: "POST",
    headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
    body: JSON.stringify({ name: "agent" }),
  });
  return (await minted.json()) as { token: string };
}

async function createTask(
  app: ReturnType<typeof createApp>,
  token: string,
  projectId: string,
  body: Record<string, unknown>,
  key: string,
) {
  return app.request(`/v1/projects/${projectId}/tasks`, {
    method: "POST",
    headers: {
      cookie: cookieHeader(token),
      "content-type": "application/json",
      "idempotency-key": key,
    },
    body: JSON.stringify(body),
  });
}

describe("milestones and tasks", () => {
  it("creates a milestone and a task, then lists and gets the task", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "board");

    const milestoneRes = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ title: "M1", description: "first cut" }),
    });
    expect(milestoneRes.status).toBe(201);
    const milestone = (await milestoneRes.json()) as { id: string; status: string };
    expect(milestone.status).toBe("open");

    const listedMilestones = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listedMilestones.status).toBe(200);
    expect(await listedMilestones.json()).toMatchObject({
      items: [expect.objectContaining({ id: milestone.id, title: "M1" })],
      next_cursor: null,
    });

    const seedKey = `beacon-dogfood:${project.id}:milestone`;
    const firstSeed = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": seedKey,
      },
      body: JSON.stringify({ title: "Dogfood A" }),
    });
    const secondSeed = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": seedKey,
      },
      body: JSON.stringify({ title: "Dogfood A" }),
    });
    expect(firstSeed.status).toBe(201);
    expect(secondSeed.status).toBe(201);
    const firstSeedBody = (await firstSeed.json()) as { id: string };
    const secondSeedBody = (await secondSeed.json()) as { id: string };
    expect(secondSeedBody.id).toBe(firstSeedBody.id);

    const created = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Ship API", type: "story", milestone_id: milestone.id },
      "create-ship-api",
    );
    expect(created.status).toBe(201);
    const task = (await created.json()) as TaskBody;
    expect(task).toMatchObject({
      title: "Ship API",
      status: "backlog",
      type: "story",
      version: 1,
      project_id: project.id,
    });

    const listed = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toMatchObject({
      items: [expect.objectContaining({ id: task.id, title: "Ship API" })],
      next_cursor: null,
    });

    const got = await alice.app.request(`/v1/tasks/${task.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(got.status).toBe(200);
    expect(await got.json()).toMatchObject({ id: task.id, version: 1 });
  });

  it("returns 409 version_conflict with the current row", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "occ");
    const created = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "OCC" },
      "create-occ",
    );
    const task = (await created.json()) as TaskBody;

    const first = await alice.app.request(`/v1/tasks/${task.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ title: "OCC v2", expected_version: 1 }),
    });
    expect(first.status).toBe(200);
    expect(await first.json()).toMatchObject({ title: "OCC v2", version: 2 });

    const stale = await alice.app.request(`/v1/tasks/${task.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ title: "stale", expected_version: 1 }),
    });
    expect(stale.status).toBe(409);
    expect(await stale.json()).toMatchObject({
      error: {
        code: "version_conflict",
        details: { task: { id: task.id, title: "OCC v2", version: 2 } },
      },
    });
  });

  it("rejects a blocking dependency cycle with 409 dependency_cycle", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "deps");
    const aRes = await createTask(alice.app, alice.token, project.id, { title: "A" }, "task-a");
    const bRes = await createTask(alice.app, alice.token, project.id, { title: "B" }, "task-b");
    const a = (await aRes.json()) as TaskBody;
    const b = (await bRes.json()) as TaskBody;

    const first = await alice.app.request(`/v1/tasks/${a.id}/dependencies`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ to_task_id: b.id, type: "blocks" }),
    });
    expect(first.status).toBe(201);

    const cycle = await alice.app.request(`/v1/tasks/${b.id}/dependencies`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ to_task_id: a.id, type: "blocks" }),
    });
    expect(cycle.status).toBe(409);
    expect(await cycle.json()).toMatchObject({ error: { code: "dependency_cycle" } });
  });

  it("returns 404 when another user reads or mutates a task", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "secret");
    const created = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Private" },
      "private-task",
    );
    const task = (await created.json()) as TaskBody;

    const get = await bob.app.request(`/v1/tasks/${task.id}`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(get.status).toBe(404);
    expect(await get.json()).toMatchObject({
      error: { code: "not_found", message: "task not found" },
    });

    const comments = await bob.app.request(`/v1/tasks/${task.id}/comments`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(comments.status).toBe(404);
    expect(await comments.json()).toMatchObject({
      error: { code: "not_found", message: "task not found" },
    });

    const patch = await bob.app.request(`/v1/tasks/${task.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(bob.token), "content-type": "application/json" },
      body: JSON.stringify({ title: "Hacked", expected_version: 1 }),
    });
    expect(patch.status).toBe(404);

    const listed = await bob.app.request(`/v1/projects/${project.id}/tasks`, {
      headers: { cookie: cookieHeader(bob.token) },
    });
    expect(listed.status).toBe(404);
  });

  it("lets a human move any status to any other status", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "flow");
    const created = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Card", status: "done" },
      "any-status",
    );
    const task = (await created.json()) as TaskBody;
    expect(task.status).toBe("done");

    const ready = await alice.app.request(`/v1/tasks/${task.id}/status`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ status: "ready", expected_version: 1 }),
    });
    expect(ready.status).toBe(200);
    expect(await ready.json()).toMatchObject({ status: "ready", version: 2 });

    const canceled = await alice.app.request(`/v1/tasks/${task.id}/status`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ status: "canceled", expected_version: 2 }),
    });
    expect(canceled.status).toBe(200);
    expect(await canceled.json()).toMatchObject({ status: "canceled", version: 3 });
  });

  it("writes comments and filters activity by object_id", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "notes");
    const first = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "One" },
      "task-one",
    );
    const second = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Two" },
      "task-two",
    );
    const task = (await first.json()) as TaskBody;
    const other = (await second.json()) as TaskBody;

    const comment = await alice.app.request(`/v1/tasks/${task.id}/comments`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "comment-1",
      },
      body: JSON.stringify({ body: "looks good" }),
    });
    expect(comment.status).toBe(201);
    expect(await comment.json()).toMatchObject({
      task_id: task.id,
      author_type: "user",
      body: "looks good",
    });

    const activity = await alice.app.request(
      `/v1/projects/${project.id}/activity?object_id=${task.id}`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    expect(activity.status).toBe(200);
    const body = (await activity.json()) as { items: { verb: string; object_id: string }[] };
    expect(body.items.every((item) => item.object_id === task.id)).toBe(true);
    expect(body.items.map((item) => item.verb)).toEqual(
      expect.arrayContaining(["create", "comment"]),
    );
    expect(body.items.some((item) => item.object_id === other.id)).toBe(false);

    const comments = await alice.app.request(`/v1/tasks/${task.id}/comments`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(comments.status).toBe(200);
    expect(await comments.json()).toMatchObject({
      items: [expect.objectContaining({ task_id: task.id, body: "looks good" })],
      next_cursor: null,
    });
  });

  it("returns the same task for a repeated Idempotency-Key", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "idem");
    const first = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Once" },
      "same-key",
    );
    const second = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Once" },
      "same-key",
    );
    expect(first.status).toBe(201);
    expect(second.status).toBe(201);
    const a = (await first.json()) as TaskBody;
    const b = (await second.json()) as TaskBody;
    expect(b.id).toBe(a.id);

    const listed = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    const page = (await listed.json()) as { items: TaskBody[] };
    expect(page.items).toHaveLength(1);
  });

  it("soft-deletes a task for admins only and hides it from later reads", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const writer = await registerUser(store, "writer");
    const project = await createProject(alice.app, alice.token, "trash");
    await alice.app.request(`/v1/projects/${project.id}/members`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ user_id: writer.user.id, role: "write" }),
    });
    const created = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Gone" },
      "delete-me",
    );
    const task = (await created.json()) as TaskBody;

    const forbidden = await writer.app.request(`/v1/tasks/${task.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(writer.token) },
    });
    expect(forbidden.status).toBe(403);

    const deleted = await alice.app.request(`/v1/tasks/${task.id}`, {
      method: "DELETE",
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(deleted.status).toBe(200);
    expect(await deleted.json()).toMatchObject({ id: task.id, deleted_at: expect.any(String) });

    const get = await alice.app.request(`/v1/tasks/${task.id}`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(get.status).toBe(404);
  });

  it("releases an agent lock on a human status change", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "locks");
    const now = new Date("2026-01-01T00:00:00.000Z");
    const locked = await store.createTask({
      id: "018f1e2c-3d4e-7000-8000-0000000000aa",
      projectId: project.id,
      milestoneId: null,
      parentId: null,
      title: "Locked",
      description: "",
      status: "in_progress",
      priority: 0,
      type: "task",
      version: 1,
      assigneeUserId: null,
      assigneeAgentName: "codex",
      agentBrief: "",
      linkedPaths: [],
      githubIssueId: null,
      lockedBySessionId: "018f1e2c-3d4e-7000-8000-0000000000bb",
      lockExpiresAt: new Date("2026-01-01T04:00:00.000Z"),
      deletedAt: null,
      createdAt: now,
      updatedAt: now,
    });

    const moved = await alice.app.request(`/v1/tasks/${locked.id}/status`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ status: "done", expected_version: 1 }),
    });
    expect(moved.status).toBe(200);
    expect(await moved.json()).toMatchObject({
      status: "done",
      locked_by_session_id: null,
      version: 2,
    });

    const activity = await alice.app.request(
      `/v1/projects/${project.id}/activity?object_id=${locked.id}`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    const body = (await activity.json()) as { items: { verb: string }[] };
    expect(body.items.map((item) => item.verb)).toEqual(
      expect.arrayContaining(["status", "lock_released"]),
    );
  });

  it("releases an agent lock on a human PATCH", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "patch-locks");
    const now = new Date("2026-01-01T00:00:00.000Z");
    const locked = await store.createTask({
      id: "018f1e2c-3d4e-7000-8000-0000000000cc",
      projectId: project.id,
      milestoneId: null,
      parentId: null,
      title: "Locked",
      description: "",
      status: "in_progress",
      priority: 0,
      type: "task",
      version: 1,
      assigneeUserId: null,
      assigneeAgentName: "codex",
      agentBrief: "",
      linkedPaths: [],
      githubIssueId: null,
      lockedBySessionId: "018f1e2c-3d4e-7000-8000-0000000000dd",
      lockExpiresAt: new Date("2026-01-01T04:00:00.000Z"),
      deletedAt: null,
      createdAt: now,
      updatedAt: now,
    });

    const patched = await alice.app.request(`/v1/tasks/${locked.id}`, {
      method: "PATCH",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ description: "edited", expected_version: 1 }),
    });
    expect(patched.status).toBe(200);
    expect(await patched.json()).toMatchObject({
      description: "edited",
      locked_by_session_id: null,
      version: 2,
    });

    const activity = await alice.app.request(
      `/v1/projects/${project.id}/activity?object_id=${locked.id}`,
      { headers: { cookie: cookieHeader(alice.token) } },
    );
    const body = (await activity.json()) as { items: { verb: string }[] };
    expect(body.items.map((item) => item.verb)).toEqual(
      expect.arrayContaining(["update", "lock_released"]),
    );
  });

  it("requires Idempotency-Key on create and comment", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "need-key");
    const created = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ title: "No key" }),
    });
    expect(created.status).toBe(400);
    expect(await created.json()).toMatchObject({
      error: { code: "unauthorized", details: { reason: "missing_idempotency_key" } },
    });

    const taskRes = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Has key" },
      "has-key",
    );
    const task = (await taskRes.json()) as TaskBody;
    const comment = await alice.app.request(`/v1/tasks/${task.id}/comments`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ body: "no key" }),
    });
    expect(comment.status).toBe(400);
    expect(await comment.json()).toMatchObject({
      error: { code: "unauthorized", details: { reason: "missing_idempotency_key" } },
    });
  });

  it("returns 409 dependency_cycle for a self-edge and allows a relates pair", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "relates");
    const aRes = await createTask(alice.app, alice.token, project.id, { title: "A" }, "rel-a");
    const bRes = await createTask(alice.app, alice.token, project.id, { title: "B" }, "rel-b");
    const a = (await aRes.json()) as TaskBody;
    const b = (await bRes.json()) as TaskBody;

    const selfEdge = await alice.app.request(`/v1/tasks/${a.id}/dependencies`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ to_task_id: a.id, type: "blocks" }),
    });
    expect(selfEdge.status).toBe(409);
    expect(await selfEdge.json()).toMatchObject({ error: { code: "dependency_cycle" } });

    const first = await alice.app.request(`/v1/tasks/${a.id}/dependencies`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ to_task_id: b.id, type: "relates" }),
    });
    expect(first.status).toBe(201);
    const reverse = await alice.app.request(`/v1/tasks/${b.id}/dependencies`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ to_task_id: a.id, type: "relates" }),
    });
    expect(reverse.status).toBe(201);
  });

  it("rejects an unknown or non-member assignee", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const bob = await registerUser(store, "bob");
    const project = await createProject(alice.app, alice.token, "assignees");

    const missing = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Ghost", assignee_user_id: "018f1e2c-3d4e-7000-8000-0000000000ee" },
      "ghost-assignee",
    );
    expect(missing.status).toBe(400);

    const outsider = await createTask(
      alice.app,
      alice.token,
      project.id,
      { title: "Outsider", assignee_user_id: bob.user.id },
      "outsider-assignee",
    );
    expect(outsider.status).toBe(400);
    expect(await outsider.json()).toMatchObject({
      error: { details: { reason: "invalid_assignee" } },
    });
  });

  it("forces non-admin tokens to create tasks in backlog", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "token-status");
    const minted = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "agent" }),
    });
    const secret = ((await minted.json()) as { token: string }).token;

    const created = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "token-in-progress",
      },
      body: JSON.stringify({ title: "Agent work", status: "in_progress" }),
    });
    expect(created.status).toBe(201);
    expect(await created.json()).toMatchObject({ title: "Agent work", status: "backlog" });
  });

  it("serializes concurrent same-key creates and reuses an expired key", async () => {
    const store = new MemoryAuthStore();
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const app = createApp({
      store,
      config: testConfig(),
      clock,
      checkReady: async () => true,
    });
    const register = await app.request("/v1/auth/register", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ login: "alice", password: STRONG_PASSWORD }),
    });
    const token = sessionCookie(register)!;
    const project = await createProject(app, token, "race-key");

    const [first, second] = await Promise.all([
      createTask(app, token, project.id, { title: "Once" }, "race"),
      createTask(app, token, project.id, { title: "Once" }, "race"),
    ]);
    expect(first.status).toBe(201);
    expect(second.status).toBe(201);
    const a = (await first.json()) as TaskBody;
    const b = (await second.json()) as TaskBody;
    expect(b.id).toBe(a.id);

    now = new Date("2026-01-02T00:00:01.000Z");
    const reused = await createTask(app, token, project.id, { title: "Again" }, "race");
    expect(reused.status).toBe(201);
    const next = (await reused.json()) as TaskBody;
    expect(next.id).not.toBe(a.id);
    expect(next.title).toBe("Again");

    const listed = await app.request(`/v1/projects/${project.id}/tasks`, {
      headers: { cookie: cookieHeader(token) },
    });
    const page = (await listed.json()) as { items: TaskBody[] };
    expect(page.items).toHaveLength(2);
  });

  it("lets a project token list, create, get, comment, and link tasks", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "agent-board");
    const minted = await mintToken(alice.app, alice.token, project.id);
    const headers = {
      authorization: `Bearer ${minted.token}`,
      "content-type": "application/json",
    };

    const milestones = await alice.app.request(`/v1/projects/${project.id}/milestones`, {
      headers: { authorization: `Bearer ${minted.token}` },
    });
    expect(milestones.status).toBe(200);

    const created = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: { ...headers, "idempotency-key": "agent-task" },
      body: JSON.stringify({ title: "Agent task", status: "ready" }),
    });
    expect(created.status).toBe(201);
    const task = (await created.json()) as TaskBody;
    expect(task.status).toBe("backlog");

    const listed = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      headers: { authorization: `Bearer ${minted.token}` },
    });
    expect(listed.status).toBe(200);
    expect(await listed.json()).toMatchObject({
      items: [expect.objectContaining({ id: task.id })],
    });

    const got = await alice.app.request(`/v1/tasks/${task.id}`, {
      headers: { authorization: `Bearer ${minted.token}` },
    });
    expect(got.status).toBe(200);
    expect(await got.json()).toMatchObject({ id: task.id, project_id: project.id });

    const comment = await alice.app.request(`/v1/tasks/${task.id}/comments`, {
      method: "POST",
      headers: { ...headers, "idempotency-key": "agent-comment" },
      body: JSON.stringify({ body: "noted" }),
    });
    expect(comment.status).toBe(201);

    const other = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: { ...headers, "idempotency-key": "agent-other" },
      body: JSON.stringify({ title: "Other" }),
    });
    const otherTask = (await other.json()) as TaskBody;
    const link = await alice.app.request(`/v1/tasks/${task.id}/dependencies`, {
      method: "POST",
      headers,
      body: JSON.stringify({ to_task_id: otherTask.id, type: "relates" }),
    });
    expect(link.status).toBe(201);
  });
});
