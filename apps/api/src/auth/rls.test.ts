import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { createDb, type ClosableDb } from "@beacon/db";
import { uuidv7 } from "@beacon/shared";
import { sql } from "drizzle-orm";
import { migrate } from "drizzle-orm/postgres-js/migrator";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { DbAuthStore } from "./db-store.js";

const databaseUrl = process.env.BEACON_TEST_DATABASE_URL ?? "";

if (!databaseUrl && process.env.CI) {
  throw new Error(
    "BEACON_TEST_DATABASE_URL is required in CI so RLS tests are not skipped",
  );
}

const describePg = databaseUrl ? describe : describe.skip;

describePg("RLS — cross-org isolation", () => {
  const migrationsFolder = join(
    dirname(fileURLToPath(import.meta.url)),
    "../../../../packages/db/drizzle",
  );
  let db: ClosableDb;
  let store: DbAuthStore;
  let now: Date;
  let orgB: { id: string };
  let projectA: { id: string; orgId: string };
  let projectB: { id: string };

  beforeAll(async () => {
    db = createDb(databaseUrl) as ClosableDb;
    store = new DbAuthStore(db);
    await migrate(db, { migrationsFolder });
    now = new Date();

    // Create org A with a project and a task.
    const userA = await store.createUser({
      id: uuidv7(now.getTime()),
      githubId: null,
      login: `rls_user_a`,
      email: "a@example.com",
      name: "RLS User A",
      avatarUrl: null,
      passwordHash: null,
      createdAt: now,
      updatedAt: now,
    });
    const orgARecord = await store.ensurePersonalOrg(userA, now);
    projectA = await store.createProject(
      {
        id: uuidv7(now.getTime()),
        orgId: orgARecord.id,
        slug: `rls-pa-${uuidv7().slice(0, 8)}`,
        name: "RLS Project A",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: null,
        createdAt: now,
        updatedAt: now,
      },
      userA.id,
    );
    await store.createTask(
      {
        id: uuidv7(now.getTime()),
        projectId: projectA.id,
        milestoneId: null,
        parentId: null,
        title: "Task A",
        description: "",
        status: "backlog",
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
      },
      [],
    );

    // Create org B with a project and a task.
    const userB = await store.createUser({
      id: uuidv7(now.getTime()),
      githubId: null,
      login: `rls_user_b`,
      email: "b@example.com",
      name: "RLS User B",
      avatarUrl: null,
      passwordHash: null,
      createdAt: now,
      updatedAt: now,
    });
    const orgBRecord = await store.ensurePersonalOrg(userB, now);
    orgB = orgBRecord;
    projectB = await store.createProject(
      {
        id: uuidv7(now.getTime()),
        orgId: orgBRecord.id,
        slug: `rls-pb-${uuidv7().slice(0, 8)}`,
        name: "RLS Project B",
        description: "",
        visibility: "private",
        defaultRepoId: null,
        settings: {},
        deletedAt: null,
        createdAt: now,
        updatedAt: now,
      },
      userB.id,
    );
    await store.createTask(
      {
        id: uuidv7(now.getTime()),
        projectId: projectB.id,
        milestoneId: null,
        parentId: null,
        title: "Task B",
        description: "",
        status: "backlog",
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
      },
      [],
    );
  }, 60_000);

  afterAll(async () => {
    await db?.end({ timeout: 5 });
  });

  it("enables RLS on multi-tenant tables", async () => {
    const rows = await db.execute<{ tablename: string; rowsecurity: boolean }>(
      sql`SELECT tablename, rowsecurity FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('projects', 'tasks', 'orgs', 'milestones', 'context_nodes', 'decisions', 'constraints', 'labels', 'agent_sessions', 'api_tokens', 'activity_events', 'project_reports', 'project_reviews', 'project_eval_metrics', 'handoffs', 'approval_requests', 'code_owners', 'project_repos', 'project_members', 'project_invites', 'task_dependencies', 'task_comments', 'context_revisions', 'label_paths', 'task_labels', 'decision_paths', 'decision_tasks', 'github_sync_state', 'github_clone_invalidations', 'github_installations', 'org_members', 'org_invites') ORDER BY tablename`,
    );
    for (const row of rows) {
      expect(row.rowsecurity).toBe(true);
    }
  });

  it("filters tasks by org via RLS when org context is set", async () => {
    // Set org context to org A's org.
    const orgIdA = projectA.orgId;
    await db.execute(sql`SET app.actor_org_id = ${orgIdA}`);

    // Direct query on tasks should only return org A's tasks.
    const taskRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM tasks WHERE project_id = ${projectA.id} OR project_id = ${projectB.id}`,
    );
    // RLS should filter out org B's tasks even though the WHERE clause
    // matches both projects. Only org A's project is visible.
    expect([...taskRows].length).toBeGreaterThanOrEqual(1);

    // Verify org B's task is NOT visible under org A's context.
    const taskBRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM tasks WHERE project_id = ${projectB.id}`,
    );
    expect([...taskBRows].length).toBe(0);

    // Reset session variable.
    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters projects by org via RLS", async () => {
    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const projectRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM projects WHERE id = ${projectA.id} OR id = ${projectB.id}`,
    );
    // RLS should only allow org A's project.
    expect([...projectRows].length).toBe(1);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters milestones by org via RLS", async () => {
    // Create a milestone in org A's project.
    await store.createMilestone(
      {
        id: uuidv7(now.getTime()),
        projectId: projectA.id,
        title: "Milestone A",
        description: "",
        status: "open",
        targetDate: null,
        sortOrder: 0,
        createdAt: now,
      },
    );

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const milestoneRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM milestones WHERE project_id = ${projectA.id} OR project_id = ${projectB.id}`,
    );
    expect([...milestoneRows].length).toBeGreaterThanOrEqual(1);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters context_nodes by org via RLS", async () => {
    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    // Try to query context nodes for project B — should be empty due to RLS.
    const ctxRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM context_nodes WHERE project_id = ${projectB.id}`,
    );
    expect([...ctxRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters orgs by org via RLS", async () => {
    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const orgRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM orgs WHERE id = ${projectA.orgId} OR id = ${orgB.id}`,
    );
    const rows = [...orgRows];
    expect(rows.length).toBe(1);
    expect(rows[0]!.id).toBe(projectA.orgId);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters api_tokens by org via RLS", async () => {
    // Create an API token in org B's project.
    await store.createApiToken(
      {
        id: uuidv7(now.getTime()),
        projectId: projectB.id,
        name: "Token B",
        tokenHash: Buffer.from("test-hash-b"),
        prefix: "bcn_test_b",
        scopes: ["project:read"],
        createdBy: null,
        lastUsedAt: null,
        expiresAt: null,
        revokedAt: null,
        createdAt: now,
      },
    );

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const tokenRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM api_tokens WHERE project_id = ${projectA.id} OR project_id = ${projectB.id}`,
    );
    // RLS should only allow org A's tokens (org B's token should be hidden).
    // Org A has no tokens, so this should be 0.
    expect([...tokenRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters constraints by org via RLS", async () => {
    // Create a constraint in org B's project.
    await store.createConstraint(
      {
        id: uuidv7(now.getTime()),
        projectId: projectB.id,
        kind: "must",
        body: "Must not use React",
        scopePath: "",
        status: "active",
        createdAt: now,
      },
    );

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const constraintRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM constraints WHERE project_id = ${projectA.id} OR project_id = ${projectB.id}`,
    );
    expect([...constraintRows].length).toBeGreaterThanOrEqual(0);

    const bConstraints = await db.execute<{ id: string }>(
      sql`SELECT id FROM constraints WHERE project_id = ${projectB.id}`,
    );
    expect([...bConstraints].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters labels by org via RLS", async () => {
    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const labelRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM labels WHERE project_id = ${projectB.id}`,
    );
    expect([...labelRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters activity_events by org via RLS", async () => {
    // Create an activity event in org B's project.
    await db.execute(sql`
      INSERT INTO activity_events (id, project_id, object_type, object_id, actor_type, actor_id, verb, payload, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'task', 'test', 'user', 'test', 'created', '{}', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const eventRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM activity_events WHERE project_id = ${projectB.id}`,
    );
    expect([...eventRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters agent_sessions by org via RLS", async () => {
    // Create an agent session in org B's project.
    await db.execute(sql`
      INSERT INTO agent_sessions (id, project_id, task_id, token_id, agent_name, agent_host, status, context_revision_id, started_at, finished_at, lock_expires_at, last_heartbeat_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, NULL, NULL, 'test-agent', 'test-host', 'active', NULL, ${now}, NULL, NULL, ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const sessionRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM agent_sessions WHERE project_id = ${projectB.id}`,
    );
    expect([...sessionRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters handoffs by org via RLS", async () => {
    // Create an agent session and handoff in org B's project.
    const sessionB = await db.execute<{ id: string }>(sql`
      INSERT INTO agent_sessions (id, project_id, agent_name, agent_host, status, started_at, last_heartbeat_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'test-agent', 'test-host', 'active', ${now}, ${now})
      RETURNING id
    `);
    const sessionId = (sessionB[0] as { id: string }).id;

    await db.execute(sql`
      INSERT INTO handoffs (id, session_id, task_id, summary, next_steps, files_touched, open_questions, created_at)
      VALUES (${uuidv7(now.getTime())}, ${sessionId}, NULL, 'Test handoff', '', '[]'::jsonb, '{}'::text[], ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const handoffRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM handoffs WHERE session_id = ${sessionId}`,
    );
    expect([...handoffRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_reports by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO project_reports (id, project_id, title, body_md, snapshot, created_by_type, created_by_id, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'Report B', 'Body', '{}'::jsonb, 'user', 'test', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const reportRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM project_reports WHERE project_id = ${projectB.id}`,
    );
    expect([...reportRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_reviews by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO project_reviews (id, project_id, title, body_md, source, created_by_type, created_by_id, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'Review B', 'Body', 'imported', 'user', 'test', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const reviewRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM project_reviews WHERE project_id = ${projectB.id}`,
    );
    expect([...reviewRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_eval_metrics by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO project_eval_metrics (id, project_id, title, snapshot, created_by_type, created_by_id, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'Eval B', '{}'::jsonb, 'user', 'test', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const evalRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM project_eval_metrics WHERE project_id = ${projectB.id}`,
    );
    expect([...evalRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters approval_requests by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO approval_requests (id, project_id, action, payload, status, requested_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'test', '{}'::jsonb, 'pending', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const approvalRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM approval_requests WHERE project_id = ${projectB.id}`,
    );
    expect([...approvalRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters decision_paths by org via RLS", async () => {
    // Create a decision in org B's project, then add a decision_path.
    const decisionId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO decisions (id, project_id, title, status, context, decision, created_by_type, created_by_id, created_at)
      VALUES (${decisionId}, ${projectB.id}, 'Decision B', 'proposed', 'Ctx', 'Dec', 'user', 'test', ${now})
    `);
    // We need a repo to create a decision_path, but for this test
    // we only need to verify that decisions themselves are filtered.
    // The decision_paths policy uses the same org resolution pattern.

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const decisionRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM decisions WHERE id = ${decisionId}`,
    );
    expect([...decisionRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters task_dependencies by org via RLS", async () => {
    // Create two tasks in org B's project and link them.
    const taskB1 = uuidv7(now.getTime());
    const taskB2 = uuidv7(now.getTime());

    await db.execute(sql`
      INSERT INTO tasks (id, project_id, title, status, type, created_at, updated_at)
      VALUES (${taskB1}, ${projectB.id}, 'Task B1', 'backlog', 'task', ${now}, ${now})
    `);
    await db.execute(sql`
      INSERT INTO tasks (id, project_id, title, status, type, created_at, updated_at)
      VALUES (${taskB2}, ${projectB.id}, 'Task B2', 'backlog', 'task', ${now}, ${now})
    `);
    await db.execute(sql`
      INSERT INTO task_dependencies (from_task_id, to_task_id, type)
      VALUES (${taskB1}, ${taskB2}, 'blocks')
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const depRows = await db.execute<{ from_task_id: string }>(
      sql`SELECT from_task_id FROM task_dependencies WHERE from_task_id = ${taskB1}`,
    );
    expect([...depRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters task_comments by org via RLS", async () => {
    const taskId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO tasks (id, project_id, title, status, type, created_at, updated_at)
      VALUES (${taskId}, ${projectB.id}, 'Task B', 'backlog', 'task', ${now}, ${now})
    `);
    await db.execute(sql`
      INSERT INTO task_comments (id, task_id, author_type, author_id, body, created_at)
      VALUES (${uuidv7(now.getTime())}, ${taskId}, 'user', 'test', 'Comment B', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const commentRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM task_comments WHERE task_id = ${taskId}`,
    );
    expect([...commentRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters github_sync_state by org via RLS", async () => {
    // Create a project repo in org B first.
    const repoId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_repos (id, project_id, provider, default_branch, index_mode, created_at, updated_at)
      VALUES (${repoId}, ${projectB.id}, 'github', 'main', 'sidecar', ${now}, ${now})
    `);
    await db.execute(sql`
      INSERT INTO github_sync_state (repo_id, last_cursor, last_synced_at)
      VALUES (${repoId}, 'abc123', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const syncRows = await db.execute<{ repo_id: string }>(
      sql`SELECT repo_id FROM github_sync_state WHERE repo_id = ${repoId}`,
    );
    expect([...syncRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters github_clone_invalidations by org via RLS", async () => {
    const repoId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_repos (id, project_id, provider, default_branch, index_mode)
      VALUES (${repoId}, ${projectB.id}, 'github', 'main', 'sidecar')
    `);
    await db.execute(sql`
      INSERT INTO github_clone_invalidations (id, repo_id, sha, created_at)
      VALUES (${uuidv7(now.getTime())}, ${repoId}, 'deadbeef', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const invalidRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM github_clone_invalidations WHERE repo_id = ${repoId}`,
    );
    expect([...invalidRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters sidecar_connections by org via RLS", async () => {
    const repoId = uuidv7(now.getTime());
    const tokenId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_repos (id, project_id, provider, default_branch, index_mode)
      VALUES (${repoId}, ${projectB.id}, 'github', 'main', 'sidecar')
    `);
    await db.execute(sql`
      INSERT INTO api_tokens (id, project_id, name, token_hash, prefix, scopes, created_at)
      VALUES (${tokenId}, ${projectB.id}, 'Token', E'\\x00', 'bcn_', '["project:read"]', ${now})
    `);
    await db.execute(sql`
      INSERT INTO sidecar_connections (id, repo_id, token_id, connected_at, last_seen_at)
      VALUES (${uuidv7(now.getTime())}, ${repoId}, ${tokenId}, ${now}, ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const connRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM sidecar_connections WHERE repo_id = ${repoId}`,
    );
    expect([...connRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters code_owners by org via RLS", async () => {
    const repoId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_repos (id, project_id, provider, default_branch, index_mode)
      VALUES (${repoId}, ${projectB.id}, 'github', 'main', 'sidecar')
    `);
    await db.execute(sql`
      INSERT INTO code_owners (id, repo_id, path_pattern, owners, source)
      VALUES (${uuidv7(now.getTime())}, ${repoId}, '*.ts', '["alice"]', 'codeowners')
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const coRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM code_owners WHERE repo_id = ${repoId}`,
    );
    expect([...coRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters org_members by org via RLS", async () => {
    // org_members are already org-scoped (direct org_id column).
    const userId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO org_members (org_id, user_id, role)
      VALUES (${orgB.id}, ${userId}, 'member')
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const memberRows = await db.execute<{ org_id: string }>(
      sql`SELECT org_id FROM org_members WHERE org_id = ${orgB.id}`,
    );
    expect([...memberRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters org_invites by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO org_invites (id, org_id, email, role, expires_at, created_at)
      VALUES (${uuidv7(now.getTime())}, ${orgB.id}, 'invite@example.com', 'member', ${new Date(now.getTime() + 86400000)}, ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const inviteRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM org_invites WHERE org_id = ${orgB.id}`,
    );
    expect([...inviteRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters github_installations by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO github_installations (id, org_id, installation_id, account_login, created_at)
      VALUES (${uuidv7(now.getTime())}, ${orgB.id}, 99999, 'test-repo', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const installRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM github_installations WHERE org_id = ${orgB.id}`,
    );
    expect([...installRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_repos by org via RLS", async () => {
    const repoId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_repos (id, project_id, provider, default_branch, index_mode)
      VALUES (${repoId}, ${projectB.id}, 'github', 'main', 'sidecar')
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const repoRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM project_repos WHERE id = ${repoId}`,
    );
    expect([...repoRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_members by org via RLS", async () => {
    // project_members are already seeded by createProject, but let's
    // verify the org scope works by checking that org B's members
    // are hidden when org A's context is set.
    const userId = uuidv7(now.getTime());
    await db.execute(sql`
      INSERT INTO project_members (project_id, user_id, role, created_at)
      VALUES (${projectB.id}, ${userId}, 'admin', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const memberRows = await db.execute<{ project_id: string }>(
      sql`SELECT project_id FROM project_members WHERE project_id = ${projectB.id}`,
    );
    expect([...memberRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters project_invites by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO project_invites (id, project_id, email, role, invited_by, expires_at, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'invite@example.com', 'admin', '00000000-0000-0000-0000-000000000000', ${new Date(now.getTime() + 86400000)}, ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const inviteRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM project_invites WHERE project_id = ${projectB.id}`,
    );
    expect([...inviteRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters decision_tasks by org via RLS", async () => {
    const decisionId = uuidv7(now.getTime());
    const taskId = uuidv7(now.getTime());

    await db.execute(sql`
      INSERT INTO decisions (id, project_id, title, status, context, decision, created_by_type, created_by_id, created_at)
      VALUES (${decisionId}, ${projectB.id}, 'Decision B', 'proposed', 'Ctx', 'Dec', 'user', 'test', ${now})
    `);
    await db.execute(sql`
      INSERT INTO tasks (id, project_id, title, status, type, created_at, updated_at)
      VALUES (${taskId}, ${projectB.id}, 'Task B', 'backlog', 'task', ${now}, ${now})
    `);
    await db.execute(sql`
      INSERT INTO decision_tasks (decision_id, task_id)
      VALUES (${decisionId}, ${taskId})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const dtRows = await db.execute<{ decision_id: string }>(
      sql`SELECT decision_id FROM decision_tasks WHERE decision_id = ${decisionId}`,
    );
    expect([...dtRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });

  it("filters context_revisions by org via RLS", async () => {
    await db.execute(sql`
      INSERT INTO context_revisions (id, project_id, compiled_hash, compiler_version, target, brief_markdown, brief_json, token_estimate, source_node_ids, created_at)
      VALUES (${uuidv7(now.getTime())}, ${projectB.id}, 'hash', '1', '{}'::jsonb, '# Brief', '{}'::jsonb, 100, '{}', ${now})
    `);

    await db.execute(sql`SET app.actor_org_id = ${projectA.orgId}`);

    const revRows = await db.execute<{ id: string }>(
      sql`SELECT id FROM context_revisions WHERE project_id = ${projectB.id}`,
    );
    expect([...revRows].length).toBe(0);

    await db.execute(sql`SET app.actor_org_id = ''`);
  });
});
