import { uuidv7 } from "@beacon/shared";
import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { hashSessionToken } from "./auth/tokens.js";
import { MemoryAuthStore } from "./auth/store.js";
import {
  ACTIVITY_RETENTION_MS,
  BRIEF_RETENTION_PER_PROJECT,
  IDEMPOTENCY_RETENTION_MS,
  USER_SESSION_RETENTION_MS,
} from "./jobs/policy.js";
import { LOCK_TTL_MS } from "./sessions/types.js";

const WORKER_TOKEN = "worker-token-for-tests";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: undefined,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    secureCookies: false,
    trustProxy: false,
    workerToken: WORKER_TOKEN,
    ...overrides,
  };
}

function revision(projectId: string, createdAt: Date, id = uuidv7(createdAt.getTime())) {
  return {
    id,
    projectId,
    compiledHash: `hash-${id}`,
    compilerVersion: "1",
    target: { repo_id: null, path: "", task_id: null },
    briefMarkdown: "brief",
    briefJson: {},
    tokenEstimate: 1,
    sourceNodeIds: [] as string[],
    sessionId: null,
    createdAt,
  };
}

describe("worker hygiene jobs", () => {
  it("rejects missing or wrong worker tokens", async () => {
    const app = createApp({
      store: new MemoryAuthStore(),
      config: testConfig(),
      checkReady: async () => true,
    });
    const missing = await app.request("/v1/jobs/retention", { method: "POST" });
    expect(missing.status).toBe(401);
    const wrong = await app.request("/v1/jobs/expire-locks", {
      method: "POST",
      headers: { authorization: "Bearer not-the-worker" },
    });
    expect(wrong.status).toBe(401);
  });

  it("keeps the last 200 briefs per project and drops older rows", async () => {
    const store = new MemoryAuthStore();
    const projectA = "11111111-1111-7111-8111-111111111111";
    const projectB = "22222222-2222-7222-8222-222222222222";
    const start = new Date("2026-01-01T00:00:00.000Z");
    for (let i = 0; i < BRIEF_RETENTION_PER_PROJECT + 3; i += 1) {
      store.seedContextRevision(
        revision(projectA, new Date(start.getTime() + i * 1000), uuidv7(start.getTime() + i)),
      );
    }
    store.seedContextRevision(revision(projectB, start, uuidv7(start.getTime() + 10_000)));
    store.seedContextRevision(
      revision(projectB, new Date(start.getTime() + 1000), uuidv7(start.getTime() + 10_001)),
    );

    const app = createApp({ store, config: testConfig(), checkReady: async () => true });
    const res = await app.request("/v1/jobs/retention", {
      method: "POST",
      headers: { authorization: `Bearer ${WORKER_TOKEN}` },
    });
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({
      activity_deleted: 0,
      briefs_deleted: 3,
      idempotency_deleted: 0,
      sessions_deleted: 0,
    });
    expect(await store.listContextRevisions(projectA)).toHaveLength(BRIEF_RETENTION_PER_PROJECT);
    expect(await store.listContextRevisions(projectB)).toHaveLength(2);
  });

  it("drops old activity, idempotency keys, and stale user sessions", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-04-01T00:00:00.000Z");
    const projectId = "33333333-3333-7333-8333-333333333333";
    store.seedActivity({
      id: uuidv7(1),
      projectId,
      objectType: "task",
      objectId: "task-1",
      actorType: "user",
      actorId: "user-1",
      verb: "status",
      payload: {},
      createdAt: new Date(now.getTime() - ACTIVITY_RETENTION_MS - 1),
    });
    store.seedActivity({
      id: uuidv7(2),
      projectId,
      objectType: "task",
      objectId: "task-1",
      actorType: "user",
      actorId: "user-1",
      verb: "status",
      payload: {},
      createdAt: now,
    });
    store.seedIdempotency(
      "user",
      "44444444-4444-7444-8444-444444444444",
      "old-key",
      new Date(now.getTime() - IDEMPOTENCY_RETENTION_MS - 1),
    );
    store.seedIdempotency("user", "44444444-4444-7444-8444-444444444444", "fresh-key", now);
    await store.createSession({
      id: uuidv7(3),
      userId: "55555555-5555-7555-8555-555555555555",
      tokenHash: hashSessionToken("expired"),
      createdAt: new Date(now.getTime() - USER_SESSION_RETENTION_MS - 2),
      lastSeenAt: new Date(now.getTime() - USER_SESSION_RETENTION_MS - 2),
      expiresAt: new Date(now.getTime() - USER_SESSION_RETENTION_MS),
      revokedAt: null,
      userAgent: null,
      ip: null,
    });
    await store.createSession({
      id: uuidv7(4),
      userId: "55555555-5555-7555-8555-555555555555",
      tokenHash: hashSessionToken("live"),
      createdAt: now,
      lastSeenAt: now,
      expiresAt: new Date(now.getTime() + USER_SESSION_RETENTION_MS),
      revokedAt: null,
      userAgent: null,
      ip: null,
    });

    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      clock: { now: () => now },
    });
    const res = await app.request("/v1/jobs/retention", {
      method: "POST",
      headers: { authorization: `Bearer ${WORKER_TOKEN}` },
    });
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({
      activity_deleted: 1,
      briefs_deleted: 0,
      idempotency_deleted: 1,
      sessions_deleted: 1,
    });
    expect(await store.listActivity(projectId)).toHaveLength(1);
    expect(await store.findSessionByTokenHash(hashSessionToken("expired"))).toBeUndefined();
    expect(await store.findSessionByTokenHash(hashSessionToken("live"))).toBeDefined();
  });

  it("releases expired locks and abandons the previous holder without leaving it active", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-01-01T05:00:00.000Z");
    const projectId = "66666666-6666-7666-8666-666666666666";
    const sessionId = "77777777-7777-7777-8777-777777777777";
    const taskId = "88888888-8888-7888-8888-888888888888";
    const startedAt = new Date(now.getTime() - LOCK_TTL_MS);
    store.putAgentSession({
      id: sessionId,
      projectId,
      taskId,
      tokenId: null,
      agentName: "agent",
      agentHost: "custom",
      status: "active",
      contextRevisionId: null,
      startedAt,
      finishedAt: null,
      lockExpiresAt: startedAt,
      lastHeartbeatAt: startedAt,
    });
    await store.createTask({
      id: taskId,
      projectId,
      milestoneId: null,
      parentId: null,
      title: "Locked",
      description: "",
      status: "in_progress",
      priority: 0,
      type: "task",
      version: 1,
      assigneeUserId: null,
      assigneeAgentName: null,
      agentBrief: "",
      linkedPaths: [],
      githubIssueId: null,
      lockedBySessionId: sessionId,
      lockExpiresAt: startedAt,
      deletedAt: null,
      createdAt: startedAt,
      updatedAt: startedAt,
    });

    const app = createApp({
      store,
      config: testConfig(),
      checkReady: async () => true,
      clock: { now: () => now },
    });
    const res = await app.request("/v1/jobs/expire-locks", {
      method: "POST",
      headers: { authorization: `Bearer ${WORKER_TOKEN}` },
    });
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ locks_released: 1, sessions_abandoned: 1 });

    const task = await store.findTaskById(taskId);
    expect(task?.lockedBySessionId).toBeNull();
    expect(task?.lockExpiresAt).toBeNull();
    const session = await store.findAgentSessionById(sessionId);
    expect(session?.status).toBe("abandoned");
    expect(session?.finishedAt?.toISOString()).toBe(now.toISOString());
  });

  it("unlinks start_work sessions before dropping ranked-out briefs", async () => {
    const store = new MemoryAuthStore();
    const projectId = "99999999-9999-7999-8999-999999999999";
    const start = new Date("2026-01-01T00:00:00.000Z");
    const oldestId = uuidv7(start.getTime());
    for (let i = 0; i < BRIEF_RETENTION_PER_PROJECT + 1; i += 1) {
      const createdAt = new Date(start.getTime() + i * 1000);
      const id = i === 0 ? oldestId : uuidv7(createdAt.getTime());
      store.seedContextRevision(revision(projectId, createdAt, id));
    }
    store.putAgentSession({
      id: "aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa",
      projectId,
      taskId: null,
      tokenId: null,
      agentName: "agent",
      agentHost: "custom",
      status: "finished",
      contextRevisionId: oldestId,
      startedAt: start,
      finishedAt: start,
      lockExpiresAt: null,
      lastHeartbeatAt: start,
    });

    const app = createApp({ store, config: testConfig(), checkReady: async () => true });
    const res = await app.request("/v1/jobs/retention", {
      method: "POST",
      headers: { authorization: `Bearer ${WORKER_TOKEN}` },
    });
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ briefs_deleted: 1 });
    expect(await store.listContextRevisions(projectId)).toHaveLength(BRIEF_RETENTION_PER_PROJECT);
    expect(
      (await store.findAgentSessionById("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"))?.contextRevisionId,
    ).toBeNull();
    expect(
      await store
        .listContextRevisions(projectId)
        .then((rows) => rows.some((row) => row.id === oldestId)),
    ).toBe(false);
  });

  it("does not abandon a session whose heartbeat renewed the lock", async () => {
    const store = new MemoryAuthStore();
    const startedAt = new Date("2026-01-01T00:00:00.000Z");
    const expireAt = new Date("2026-01-01T05:00:00.000Z");
    const projectId = "bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb";
    const sessionId = "cccccccc-cccc-7ccc-8ccc-cccccccccccc";
    const taskId = "dddddddd-dddd-7ddd-8ddd-dddddddddddd";
    store.putAgentSession({
      id: sessionId,
      projectId,
      taskId,
      tokenId: null,
      agentName: "agent",
      agentHost: "custom",
      status: "active",
      contextRevisionId: null,
      startedAt,
      finishedAt: null,
      lockExpiresAt: startedAt,
      lastHeartbeatAt: startedAt,
    });
    await store.createTask({
      id: taskId,
      projectId,
      milestoneId: null,
      parentId: null,
      title: "Locked",
      description: "",
      status: "in_progress",
      priority: 0,
      type: "task",
      version: 1,
      assigneeUserId: null,
      assigneeAgentName: null,
      agentBrief: "",
      linkedPaths: [],
      githubIssueId: null,
      lockedBySessionId: sessionId,
      lockExpiresAt: startedAt,
      deletedAt: null,
      createdAt: startedAt,
      updatedAt: startedAt,
    });

    const renewed = await store.heartbeatSession(sessionId, expireAt);
    const counts = await store.expireLocks(expireAt);
    expect(renewed?.status).toBe("active");
    expect(renewed?.lockExpiresAt?.getTime()).toBe(expireAt.getTime() + LOCK_TTL_MS);
    expect(counts).toEqual({ locksReleased: 0, sessionsAbandoned: 0 });
    expect((await store.findAgentSessionById(sessionId))?.status).toBe("active");
    expect((await store.findTaskById(taskId))?.lockedBySessionId).toBe(sessionId);
    expect((await store.findTaskById(taskId))?.lockExpiresAt?.getTime()).toBe(
      expireAt.getTime() + LOCK_TTL_MS,
    );
  });
});
