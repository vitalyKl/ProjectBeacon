import { sql } from "drizzle-orm";
import {
  type AnyPgColumn,
  check,
  index,
  pgTable,
  primaryKey,
  text,
  uuid,
} from "drizzle-orm/pg-core";
import { timestamptz } from "./common.js";
import { projectRepos, projects } from "./projects.js";
import { tasks } from "./tasks.js";

export const decisions = pgTable(
  "decisions",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    title: text("title").notNull(),
    status: text("status").notNull(),
    context: text("context").notNull(),
    decision: text("decision").notNull(),
    consequences: text("consequences").notNull().default(""),
    createdByType: text("created_by_type").notNull(),
    createdById: text("created_by_id").notNull(),
    supersededBy: uuid("superseded_by").references((): AnyPgColumn => decisions.id),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("decisions_project").on(t.projectId, t.status),
    check(
      "decisions_status_check",
      sql`${t.status} IN ('proposed','accepted','superseded','deprecated')`,
    ),
  ],
);

export const decisionPaths = pgTable(
  "decision_paths",
  {
    decisionId: uuid("decision_id")
      .notNull()
      .references(() => decisions.id, { onDelete: "cascade" }),
    repoId: uuid("repo_id")
      .notNull()
      .references(() => projectRepos.id),
    path: text("path").notNull(),
  },
  (t) => [
    primaryKey({ columns: [t.decisionId, t.repoId, t.path] }),
    index("decision_paths_lookup").on(t.repoId, t.path),
  ],
);

export const decisionTasks = pgTable(
  "decision_tasks",
  {
    decisionId: uuid("decision_id")
      .notNull()
      .references(() => decisions.id, { onDelete: "cascade" }),
    taskId: uuid("task_id")
      .notNull()
      .references(() => tasks.id, { onDelete: "cascade" }),
  },
  (t) => [
    primaryKey({ columns: [t.decisionId, t.taskId] }),
    index("decision_tasks_task").on(t.taskId),
  ],
);

export const constraints = pgTable(
  "constraints",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    kind: text("kind").notNull(),
    body: text("body").notNull(),
    scopePath: text("scope_path").notNull().default(""),
    status: text("status").notNull().default("proposed"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("constraints_project").on(t.projectId, t.status),
    check("constraints_kind_check", sql`${t.kind} IN ('must','must_not','security','compliance')`),
    check("constraints_status_check", sql`${t.status} IN ('proposed','active','rejected')`),
  ],
);
