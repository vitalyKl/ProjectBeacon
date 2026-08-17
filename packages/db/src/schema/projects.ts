import { sql } from "drizzle-orm";
import {
  type AnyPgColumn,
  bigint,
  check,
  foreignKey,
  jsonb,
  pgTable,
  primaryKey,
  text,
  unique,
  uniqueIndex,
  uuid,
} from "drizzle-orm/pg-core";
import { timestamptz } from "./common.js";
import { orgs, users } from "./identity.js";

export const projectRepos = pgTable(
  "project_repos",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references((): AnyPgColumn => projects.id),
    provider: text("provider").notNull(),
    remoteUrl: text("remote_url"),
    defaultBranch: text("default_branch").notNull().default("main"),
    githubRepoId: bigint("github_repo_id", { mode: "bigint" }),
    installationId: bigint("installation_id", { mode: "bigint" }),
    localRootHint: text("local_root_hint"),
    indexMode: text("index_mode").notNull().default("sidecar"),
    lastIndexedSha: text("last_indexed_sha"),
    lastIndexedAt: timestamptz("last_indexed_at"),
  },
  (t) => [
    unique("project_repos_project_id_github_repo_id_unique").on(t.projectId, t.githubRepoId),
    uniqueIndex("project_repos_local_root")
      .on(t.projectId, t.localRootHint)
      .where(sql`${t.provider} = 'local' AND ${t.localRootHint} IS NOT NULL`),
    check("project_repos_provider_check", sql`${t.provider} IN ('github','local')`),
    check(
      "project_repos_index_mode_check",
      sql`${t.indexMode} IN ('sidecar','bind_mount','hosted_clone','both')`,
    ),
  ],
);

export const projects = pgTable(
  "projects",
  {
    id: uuid("id").primaryKey(),
    orgId: uuid("org_id")
      .notNull()
      .references(() => orgs.id),
    slug: text("slug").notNull(),
    name: text("name").notNull(),
    description: text("description").notNull().default(""),
    visibility: text("visibility").notNull().default("private"),
    defaultRepoId: uuid("default_repo_id"),
    settings: jsonb("settings").notNull().default(sql`'{}'::jsonb`),
    deletedAt: timestamptz("deleted_at"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
    updatedAt: timestamptz("updated_at").notNull().defaultNow(),
  },
  (t) => [
    unique("projects_org_id_slug_unique").on(t.orgId, t.slug),
    check("projects_visibility_check", sql`${t.visibility} IN ('private')`),
    foreignKey({
      name: "projects_default_repo_fk",
      columns: [t.defaultRepoId],
      foreignColumns: [projectRepos.id],
    }).onDelete("set null"),
  ],
);

export const projectMembers = pgTable(
  "project_members",
  {
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    userId: uuid("user_id")
      .notNull()
      .references(() => users.id),
    role: text("role").notNull(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.projectId, t.userId] }),
    check("project_members_role_check", sql`${t.role} IN ('admin','write','read')`),
  ],
);

export const projectInvites = pgTable(
  "project_invites",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    email: text("email"),
    githubLogin: text("github_login"),
    role: text("role").notNull(),
    invitedBy: uuid("invited_by")
      .notNull()
      .references(() => users.id),
    expiresAt: timestamptz("expires_at").notNull(),
    acceptedAt: timestamptz("accepted_at"),
  },
  (t) => [check("project_invites_role_check", sql`${t.role} IN ('admin','write','read')`)],
);

export const codeOwners = pgTable(
  "code_owners",
  {
    id: uuid("id").primaryKey(),
    repoId: uuid("repo_id")
      .notNull()
      .references(() => projectRepos.id, { onDelete: "cascade" }),
    pathPattern: text("path_pattern").notNull(),
    owners: text("owners").array().notNull(),
    source: text("source").notNull().default("codeowners"),
  },
  (t) => [unique("code_owners_repo_id_path_pattern_unique").on(t.repoId, t.pathPattern)],
);
