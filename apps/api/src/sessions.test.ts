import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";
const SUMMARY = "Finished the first cut of the API session routes.";

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

async function bootstrapWithProject(options: { clock?: { now: () => Date } } = {}) {
  const store = new MemoryAuthStore();
  const app = createApp({
    store,
    config: testConfig(),
    checkReady: async () => true,
    clock: options.clock,
  });
  const boot = await app.request("/v1/auth/bootstrap", {
    method: "POST",
    headers: {
      authorization: `Bearer ${BOOTSTRAP_TOKEN}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({ login: "admin", password: STRONG_PASSWORD }),
  });
  const cookie = sessionCookie(boot)!;
  const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(cookie) } });
  const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
  const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
    method: "POST",
    headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
    body: JSON.stringify({ slug: "beacon", name: "Beacon" }),
  });
  const project = (await created.json()) as { id: string };
  const taskRes = await app.request(`/v1/projects/${project.id}/tasks`, {
    method: "POST",
    headers: {
      cookie: cookieHeader(cookie),
      "content-type": "application/json",
      "idempotency-key": "create-task",
    },
    body: JSON.stringify({ title: "Ship sessions" }),
  });
  const task = (await taskRes.json()) as { id: string; version: number };
  return { app, store, cookie, projectId: project.id, task };
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
  return ((await minted.json()) as { token: string }).token;
}

describe("agent sessions", () => {
  it("returns 200 from start_work when CodeGateway is down", async () => {
    const { app, store, cookie, projectId, task } = await bootstrapWithProject();
    const secret = await mintToken(app, cookie, projectId);

    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "start-1",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(started.status).toBe(200);
    const body = (await started.json()) as {
      session: { id: string; task_id: string; status: string; context_revision_id: string };
      brief: {
        changed_scope: unknown;
        tree_capsule: unknown;
        budget: { dropped: string[] };
        revision_id: string;
      };
    };
    expect(body.session.task_id).toBe(task.id);
    expect(body.session.status).toBe("active");
    expect(body.brief.changed_scope).toBeNull();
    expect(body.brief.tree_capsule).toBeNull();
    expect(body.brief.budget.dropped).toEqual(
      expect.arrayContaining(["changed_scope", "tree_capsule"]),
    );
    expect(body.session.context_revision_id).toBe(body.brief.revision_id);

    const revisions = await store.listContextRevisions(projectId);
    expect(revisions).toHaveLength(1);
    expect(revisions[0]?.id).toBe(body.brief.revision_id);
    expect(revisions[0]?.sessionId).toBe(body.session.id);

    const locked = await store.findTaskById(task.id);
    expect(locked?.lockedBySessionId).toBe(body.session.id);
    expect(locked?.lockExpiresAt).not.toBeNull();
  });

  it("returns 409 task_locked unless steal=true", async () => {
    const { app, cookie, projectId, task } = await bootstrapWithProject();
    const first = await mintToken(app, cookie, projectId);
    const second = await mintToken(app, cookie, projectId);

    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${first}`,
        "content-type": "application/json",
        "idempotency-key": "start-first",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(started.status).toBe(200);

    const locked = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${second}`,
        "content-type": "application/json",
        "idempotency-key": "start-second",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(locked.status).toBe(409);
    expect(await locked.json()).toMatchObject({ error: { code: "task_locked" } });

    const stolen = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${second}`,
        "content-type": "application/json",
        "idempotency-key": "start-steal",
      },
      body: JSON.stringify({ task_id: task.id, steal: true }),
    });
    expect(stolen.status).toBe(200);
  });

  it("heartbeats, finishes with a handoff, and rejects agent terminal set_status", async () => {
    const { app, store, cookie, projectId, task } = await bootstrapWithProject();
    const secret = await mintToken(app, cookie, projectId);
    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "start-finish",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    const session = ((await started.json()) as { session: { id: string } }).session;

    const beat = await app.request(`/v1/sessions/${session.id}/heartbeat`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(beat.status).toBe(200);

    const denied = await app.request(`/v1/tasks/${task.id}/status`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ status: "done", expected_version: 1 }),
    });
    expect(denied.status).toBe(409);
    expect(await denied.json()).toMatchObject({ error: { code: "finish_work_required" } });

    const finished = await app.request(`/v1/sessions/${session.id}/finish`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ summary: SUMMARY }),
    });
    expect(finished.status).toBe(200);
    const body = (await finished.json()) as {
      session: { status: string };
      handoff: { summary: string; task_id: string };
      task: { status: string; locked_by_session_id: string | null };
    };
    expect(body.session.status).toBe("finished");
    expect(body.handoff.summary).toBe(SUMMARY);
    expect(body.handoff.task_id).toBe(task.id);
    expect(body.task).toMatchObject({ status: "done", locked_by_session_id: null });

    const handoff = await app.request(`/v1/tasks/${task.id}/handoff`, {
      headers: { authorization: `Bearer ${secret}` },
    });
    expect(handoff.status).toBe(200);
    expect(await handoff.json()).toMatchObject({ summary: SUMMARY, task_id: task.id });

    const after = await store.findTaskById(task.id);
    expect(after?.lockedBySessionId).toBeNull();
    expect(after?.status).toBe("done");
  });

  it("lets a human cookie session drag a locked task to done", async () => {
    const { app, cookie, projectId, task } = await bootstrapWithProject();
    const secret = await mintToken(app, cookie, projectId);
    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "start-human",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(started.status).toBe(200);

    const moved = await app.request(`/v1/tasks/${task.id}/status`, {
      method: "POST",
      headers: { cookie: cookieHeader(cookie), "content-type": "application/json" },
      body: JSON.stringify({ status: "done", expected_version: 1 }),
    });
    expect(moved.status).toBe(200);
    expect(await moved.json()).toMatchObject({
      status: "done",
      locked_by_session_id: null,
    });
  });

  it("abandons an expired lock holder and rejects that session's finish", async () => {
    let now = new Date("2026-01-01T00:00:00.000Z");
    const clock = { now: () => now };
    const { app, store, cookie, projectId, task } = await bootstrapWithProject({ clock });
    const first = await mintToken(app, cookie, projectId);
    const second = await mintToken(app, cookie, projectId);

    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${first}`,
        "content-type": "application/json",
        "idempotency-key": "start-expire",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    const holder = ((await started.json()) as { session: { id: string } }).session;

    now = new Date("2026-01-01T05:00:00.000Z");
    const takeover = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${second}`,
        "content-type": "application/json",
        "idempotency-key": "start-takeover",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(takeover.status).toBe(200);
    const next = ((await takeover.json()) as { session: { id: string } }).session;

    const abandoned = await store.findAgentSessionById(holder.id);
    expect(abandoned?.status).toBe("abandoned");
    const locked = await store.findTaskById(task.id);
    expect(locked?.lockedBySessionId).toBe(next.id);

    const finish = await app.request(`/v1/sessions/${holder.id}/finish`, {
      method: "POST",
      headers: { authorization: `Bearer ${first}`, "content-type": "application/json" },
      body: JSON.stringify({ summary: SUMMARY }),
    });
    expect(finish.status).toBe(409);
    expect(await finish.json()).toMatchObject({
      error: { code: "version_conflict", details: { reason: "session_inactive" } },
    });
    expect((await store.findTaskById(task.id))?.status).toBe("backlog");
  });

  it("rejects a second finish on an already finished session", async () => {
    const { app, store, cookie, projectId, task } = await bootstrapWithProject();
    const secret = await mintToken(app, cookie, projectId);
    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "start-once",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    const session = ((await started.json()) as { session: { id: string } }).session;

    const first = await app.request(`/v1/sessions/${session.id}/finish`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ summary: SUMMARY }),
    });
    expect(first.status).toBe(200);
    const version = (await store.findTaskById(task.id))?.version;

    const second = await app.request(`/v1/sessions/${session.id}/finish`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({ summary: "A second finish must not rewrite the task status now." }),
    });
    expect(second.status).toBe(409);
    expect((await store.findTaskById(task.id))?.version).toBe(version);
  });

  it("releases the lock when an admin token sets a terminal status", async () => {
    const { app, cookie, projectId, task } = await bootstrapWithProject();
    const agent = await mintToken(app, cookie, projectId);
    const admin = await mintToken(app, cookie, projectId, ["admin"]);
    const started = await app.request(`/v1/projects/${projectId}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${agent}`,
        "content-type": "application/json",
        "idempotency-key": "start-admin",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    expect(started.status).toBe(200);

    const moved = await app.request(`/v1/tasks/${task.id}/status`, {
      method: "POST",
      headers: { authorization: `Bearer ${admin}`, "content-type": "application/json" },
      body: JSON.stringify({ status: "done", expected_version: 1 }),
    });
    expect(moved.status).toBe(200);
    expect(await moved.json()).toMatchObject({
      status: "done",
      locked_by_session_id: null,
    });
  });
});
