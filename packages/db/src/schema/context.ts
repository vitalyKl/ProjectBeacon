import { sql } from "drizzle-orm";
import {
  check,
  foreignKey,
  index,
  integer,
  jsonb,
  pgTable,
  text,
  uniqueIndex,
  uuid,
} from "drizzle-orm/pg-core";
import { timestamptz } from "./common.js";
import { projectRepos, projects } from "./projects.js";
import { tasks } from "./tasks.js";

export const contextNodes = pgTable(
  "context_nodes",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    repoId: uuid("repo_id").references(() => projectRepos.id),
    taskId: uuid("task_id"),
    scopeType: text("scope_type").notNull(),
    path: text("path").notNull().default(""),
    sections: jsonb("sections").notNull().default(sql`'[]'::jsonb`),
    sectionsText: text("sections_text").notNull().default(""),
    source: text("source").notNull(),
    sourcePath: text("source_path"),
    reviewState: text("review_state").notNull().default("reviewed"),
    updatedByType: text("updated_by_type").notNull(),
    updatedById: text("updated_by_id").notNull(),
    updatedAt: timestamptz("updated_at").notNull().defaultNow(),
  },
  (t) => [
    index("context_nodes_lookup").on(t.projectId, t.scopeType, t.path),
    uniqueIndex("context_nodes_unique_scope").on(
      t.projectId,
      t.scopeType,
      sql`COALESCE(${t.repoId}, '00000000-0000-0000-0000-000000000000')`,
      t.path,
      sql`COALESCE(${t.taskId}, '00000000-0000-0000-0000-000000000000')`,
    ),
    index("context_nodes_fts").using(
      "gin",
      sql`to_tsvector('simple', coalesce(${t.sectionsText}, ''))`,
    ),
    check("context_nodes_scope_type_check", sql`${t.scopeType} IN ('project','repo','path','task')`),
    check(
      "context_nodes_review_state_check",
      sql`${t.reviewState} IN ('reviewed','needs_review')`,
    ),
    foreignKey({
      name: "context_nodes_task_fk",
      columns: [t.taskId],
      foreignColumns: [tasks.id],
    }),
  ],
);

export const contextRevisions = pgTable(
  "context_revisions",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    compiledHash: text("compiled_hash").notNull(),
    compilerVersion: text("compiler_version").notNull(),
    target: jsonb("target").notNull(),
    briefMarkdown: text("brief_markdown").notNull(),
    briefJson: jsonb("brief_json").notNull(),
    tokenEstimate: integer("token_estimate").notNull(),
    sourceNodeIds: uuid("source_node_ids").array().notNull(),
    sessionId: uuid("session_id"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [index("context_revisions_project_time").on(t.projectId, t.createdAt.desc())],
);
