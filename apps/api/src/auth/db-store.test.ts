import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { createDb } from "@beacon/db";
import { uuidv7 } from "@beacon/shared";
import { sql } from "drizzle-orm";
import { migrate } from "drizzle-orm/postgres-js/migrator";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import type { TaskRecord } from "../roadmap/types.js";
import { DbAuthStore } from "./db-store.js";
import { UniqueViolationError, VersionConflictError } from "./store.js";

const databaseUrl = process.env.BEACON_TEST_DATABASE_URL ?? "";

if (!databaseUrl && process.env.CI) {
  throw new Error(
    "BEACON_TEST_DATABASE_URL is required in CI so DbAuthStore Postgres tests are not skipped",
  );
}

const describePg = databaseUrl ? describe : describe.skip;

function newTask(projectId: string, title: string, now: Date): TaskRecord {
  return {
    id: uuidv7(now.getTime()),
    projectId,
    milestoneId: null,
    parentId: null,
    title,
    description: "",
    status: "ready",
    priority: 0,
    type: "task",
    version: 1,
    assigneeUserId: null,
    assigneeAgentName: null,
    agentBrief: "",
    howToCheck: "",
    linkedPaths: [],
    githubIssueId: null,
    lockedBySessionId: null,
    lockExpiresAt: null,
    deletedAt: null,
    createdAt: now,
    updatedAt: now,
  };
}

describePg("DbAuthStore against Postgres", () => {
  const migrationsFolder = join(
    dirname(fileURLToPath(import.meta.url)),
    "../../../../packages/db/drizzle",
  );
  let db: ReturnType<typeof createDb>;
  let store: DbAuthStore;

  beforeAll(async () => {
    db = createDb(databaseUrl);
    store = new DbAuthStore(db);
    await migrate(db, { migrationsFolder });
  }, 60_000);

  afterAll(async () => {
    await db?.end({ timeout: 5 });
  });

  async function seedProject(now: Date) {
    const userId = uuidv7();
    const user = await store.createUser({
      id: userId,
      githubId: null,
      login: `pg_${userId.replaceAll("-", "")}`,
      email: null,
      name: null,
      avatarUrl: null,
      passwordHash: null,
      createdAt: now,
      updatedAt: now,
    });
    const org = await store.ensurePersonalOrg(user, now);
    const project = await store.createProject(
      {
        id: uuidv7(),
        orgId: org.id,
        slug: `p-${userId.slice(0, 8)}`,
        name: "Postgres",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: null,
        createdAt: now,
        updatedAt: now,
      },
      user.id,
    );
    return { user, org, project };
  }

  it("has the GitHub-issue unique index that migrate must apply", async () => {
    const rows = await db.execute<{ indexname: string }>(
      sql`SELECT indexname FROM pg_indexes WHERE indexname = 'tasks_project_github_issue_id_unique'`,
    );
    expect([...rows].map((row) => row.indexname)).toEqual(["tasks_project_github_issue_id_unique"]);
  });

  it("rejects a second task with the same GitHub issue id", async () => {
    const now = new Date();
    const { project } = await seedProject(now);
    const first = await store.createTask(newTask(project.id, "One", now));
    const second = await store.createTask(newTask(project.id, "Two", now));
    const linked = await store.updateTask(first.id, first.version, { githubIssueId: 9001n }, now);
    expect(linked?.task.githubIssueId).toBe(9001n);

    await expect(
      store.updateTask(second.id, second.version, { githubIssueId: 9001n }, now),
    ).rejects.toMatchObject({ name: "UniqueViolationError" });
    await expect(
      store.updateTask(second.id, second.version, { githubIssueId: 9001n }, now),
    ).rejects.toBeInstanceOf(UniqueViolationError);
    expect((await store.findTaskById(second.id))?.githubIssueId).toBeNull();
  });

  it("throws VersionConflictError on a stale task update", async () => {
    const now = new Date();
    const { project } = await seedProject(now);
    const task = await store.createTask(newTask(project.id, "Lock", now));
    const first = await store.updateTask(task.id, task.version, { title: "First" }, now);
    expect(first?.task.version).toBe(task.version + 1);

    await expect(
      store.updateTask(task.id, task.version, { title: "Stale" }, now),
    ).rejects.toBeInstanceOf(VersionConflictError);
    expect((await store.findTaskById(task.id))?.title).toBe("First");
  });

  it("cascades task_labels when a label row is deleted", async () => {
    const now = new Date();
    const { project } = await seedProject(now);
    const labels = await store.listLabels(project.id);
    const label = labels[0];
    expect(label).toBeDefined();
    if (!label) {
      throw new Error("createProject must seed starter labels");
    }
    const task = await store.createTask(newTask(project.id, "Labeled", now), [label.id]);
    expect((await store.listTaskLabels(task.id)).map((item) => item.id)).toEqual([label.id]);

    await db.execute(sql`DELETE FROM labels WHERE id = ${label.id}`);
    expect(await store.findLabelById(label.id)).toBeUndefined();
    expect(await store.listTaskLabels(task.id)).toEqual([]);
    expect(await store.findTaskById(task.id)).toMatchObject({ id: task.id, deletedAt: null });
  });
});
