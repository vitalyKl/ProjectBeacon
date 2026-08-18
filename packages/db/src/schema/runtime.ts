import { sql } from "drizzle-orm";
import {
  bigint,
  check,
  index,
  integer,
  jsonb,
  pgTable,
  primaryKey,
  text,
  unique,
  uuid,
} from "drizzle-orm/pg-core";
import { bytea, timestamptz } from "./common.js";
import { contextRevisions } from "./context.js";
import { orgs, users } from "./identity.js";
import { projectRepos, projects } from "./projects.js";
import { tasks } from "./tasks.js";

export const apiTokens = pgTable("api_tokens", {
  id: uuid("id").primaryKey(),
  projectId: uuid("project_id")
    .notNull()
    .references(() => projects.id),
  name: text("name").notNull(),
  tokenHash: bytea("token_hash").notNull().unique(),
  prefix: text("prefix").notNull(),
  scopes: text("scopes").array().notNull(),
  createdBy: uuid("created_by").references(() => users.id),
  lastUsedAt: timestamptz("last_used_at"),
  expiresAt: timestamptz("expires_at"),
  revokedAt: timestamptz("revoked_at"),
  createdAt: timestamptz("created_at").notNull().defaultNow(),
});

export const agentSessions = pgTable(
  "agent_sessions",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    taskId: uuid("task_id").references(() => tasks.id),
    tokenId: uuid("token_id").references(() => apiTokens.id),
    agentName: text("agent_name").notNull(),
    agentHost: text("agent_host").notNull(),
    status: text("status").notNull(),
    contextRevisionId: uuid("context_revision_id").references(() => contextRevisions.id),
    startedAt: timestamptz("started_at").notNull().defaultNow(),
    finishedAt: timestamptz("finished_at"),
    lockExpiresAt: timestamptz("lock_expires_at"),
    lastHeartbeatAt: timestamptz("last_heartbeat_at").notNull().defaultNow(),
  },
  (t) => [
    index("agent_sessions_project").on(t.projectId, t.startedAt.desc()),
    index("agent_sessions_active")
      .on(t.taskId)
      .where(sql`${t.status} = 'active'`),
    check(
      "agent_sessions_status_check",
      sql`${t.status} IN ('active','paused','finished','abandoned')`,
    ),
  ],
);

export const handoffs = pgTable(
  "handoffs",
  {
    id: uuid("id").primaryKey(),
    sessionId: uuid("session_id")
      .notNull()
      .references(() => agentSessions.id),
    taskId: uuid("task_id").references(() => tasks.id),
    summary: text("summary").notNull(),
    nextSteps: text("next_steps").notNull().default(""),
    filesTouched: jsonb("files_touched")
      .notNull()
      .default(sql`'[]'::jsonb`),
    openQuestions: text("open_questions")
      .array()
      .notNull()
      .default(sql`'{}'`),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [index("handoffs_task").on(t.taskId, t.createdAt.desc())],
);

export const activityEvents = pgTable(
  "activity_events",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    objectType: text("object_type").notNull(),
    objectId: text("object_id").notNull(),
    actorType: text("actor_type").notNull(),
    actorId: text("actor_id").notNull(),
    verb: text("verb").notNull(),
    payload: jsonb("payload")
      .notNull()
      .default(sql`'{}'::jsonb`),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("activity_project_time").on(t.projectId, t.createdAt.desc()),
    index("activity_object").on(t.projectId, t.objectType, t.objectId, t.createdAt.desc()),
  ],
);

export const approvalRequests = pgTable(
  "approval_requests",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    sessionId: uuid("session_id").references(() => agentSessions.id),
    action: text("action").notNull(),
    payload: jsonb("payload").notNull(),
    status: text("status").notNull(),
    requestedAt: timestamptz("requested_at").notNull().defaultNow(),
    resolvedAt: timestamptz("resolved_at"),
    resolvedBy: uuid("resolved_by").references(() => users.id),
  },
  (t) => [
    check(
      "approval_requests_status_check",
      sql`${t.status} IN ('pending','approved','denied','expired')`,
    ),
  ],
);

export const githubInstallations = pgTable("github_installations", {
  id: uuid("id").primaryKey(),
  orgId: uuid("org_id")
    .notNull()
    .references(() => orgs.id),
  installationId: bigint("installation_id", { mode: "bigint" }).notNull().unique(),
  accountLogin: text("account_login").notNull(),
  createdAt: timestamptz("created_at").notNull().defaultNow(),
});

export const githubSyncState = pgTable("github_sync_state", {
  repoId: uuid("repo_id")
    .primaryKey()
    .references(() => projectRepos.id),
  lastCursor: text("last_cursor"),
  lastSyncedAt: timestamptz("last_synced_at"),
});

export const githubCloneInvalidations = pgTable(
  "github_clone_invalidations",
  {
    id: uuid("id").primaryKey(),
    repoId: uuid("repo_id")
      .notNull()
      .references(() => projectRepos.id, { onDelete: "cascade" }),
    sha: text("sha"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
    consumedAt: timestamptz("consumed_at"),
  },
  (t) => [index("github_clone_invalidations_repo_pending").on(t.repoId, t.createdAt)],
);

export const idempotencyKeys = pgTable(
  "idempotency_keys",
  {
    actorType: text("actor_type").notNull(),
    actorId: uuid("actor_id").notNull(),
    key: text("key").notNull(),
    response: jsonb("response").notNull(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.actorType, t.actorId, t.key] }),
    check("idempotency_keys_actor_type_check", sql`${t.actorType} IN ('token','user')`),
  ],
);

export const sidecarConnections = pgTable(
  "sidecar_connections",
  {
    id: uuid("id").primaryKey(),
    repoId: uuid("repo_id")
      .notNull()
      .references(() => projectRepos.id),
    tokenId: uuid("token_id")
      .notNull()
      .references(() => apiTokens.id),
    connectedAt: timestamptz("connected_at").notNull().defaultNow(),
    lastSeenAt: timestamptz("last_seen_at").notNull().defaultNow(),
  },
  (t) => [unique("sidecar_connections_repo_id_unique").on(t.repoId)],
);

export const rateBuckets = pgTable("rate_buckets", {
  bucketKey: text("bucket_key").primaryKey(),
  windowStart: timestamptz("window_start").notNull(),
  count: integer("count").notNull(),
  bytes: bigint("bytes", { mode: "bigint" })
    .notNull()
    .default(sql`0`),
});
