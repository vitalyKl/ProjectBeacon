import { sql } from "drizzle-orm";
import {
  type AnyPgColumn,
  bigint,
  check,
  date,
  index,
  integer,
  jsonb,
  pgTable,
  primaryKey,
  text,
  uniqueIndex,
  uuid,
} from "drizzle-orm/pg-core";
import { timestamptz } from "./common.js";
import { users } from "./identity.js";
import { projects } from "./projects.js";

export const milestones = pgTable(
  "milestones",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    title: text("title").notNull(),
    description: text("description").notNull().default(""),
    status: text("status").notNull().default("open"),
    targetDate: date("target_date"),
    sortOrder: integer("sort_order").notNull().default(0),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("milestones_project").on(t.projectId, t.sortOrder),
    check("milestones_status_check", sql`${t.status} IN ('open','closed')`),
  ],
);

export const tasks = pgTable(
  "tasks",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    milestoneId: uuid("milestone_id").references(() => milestones.id),
    parentId: uuid("parent_id").references((): AnyPgColumn => tasks.id),
    title: text("title").notNull(),
    description: text("description").notNull().default(""),
    status: text("status").notNull().default("backlog"),
    priority: integer("priority").notNull().default(0),
    type: text("type").notNull().default("task"),
    version: integer("version").notNull().default(1),
    assigneeUserId: uuid("assignee_user_id").references(() => users.id),
    assigneeAgentName: text("assignee_agent_name"),
    agentBrief: text("agent_brief").notNull().default(""),
    howToCheck: text("how_to_check").notNull().default(""),
    linkedPaths: jsonb("linked_paths").notNull().default(sql`'[]'::jsonb`),
    githubIssueId: bigint("github_issue_id", { mode: "bigint" }),
    lockedBySessionId: uuid("locked_by_session_id"),
    lockExpiresAt: timestamptz("lock_expires_at"),
    deletedAt: timestamptz("deleted_at"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
    updatedAt: timestamptz("updated_at").notNull().defaultNow(),
  },
  (t) => [
    index("tasks_project_status")
      .on(t.projectId, t.status)
      .where(sql`${t.deletedAt} IS NULL`),
    index("tasks_project_milestone")
      .on(t.projectId, t.milestoneId)
      .where(sql`${t.deletedAt} IS NULL`),
    index("tasks_updated").on(t.projectId, t.updatedAt.desc(), t.id.desc()),
    uniqueIndex("tasks_project_github_issue_id_unique")
      .on(t.projectId, t.githubIssueId)
      .where(sql`${t.githubIssueId} IS NOT NULL`),
    index("tasks_title_fts").using(
      "gin",
      sql`to_tsvector('simple', ${t.title} || ' ' || ${t.description})`,
    ),
    check(
      "tasks_status_check",
      sql`${t.status} IN (
        'backlog','ready','in_progress','blocked',
        'in_review','done','canceled'
      )`,
    ),
    check("tasks_type_check", sql`${t.type} IN ('epic','story','task','bug')`),
  ],
);

export const taskDependencies = pgTable(
  "task_dependencies",
  {
    fromTaskId: uuid("from_task_id")
      .notNull()
      .references(() => tasks.id),
    toTaskId: uuid("to_task_id")
      .notNull()
      .references(() => tasks.id),
    type: text("type").notNull(),
  },
  (t) => [
    primaryKey({ columns: [t.fromTaskId, t.toTaskId, t.type] }),
    check("task_dependencies_type_check", sql`${t.type} IN ('blocks','relates')`),
    check("task_dependencies_no_self_check", sql`${t.fromTaskId} <> ${t.toTaskId}`),
  ],
);

export const taskComments = pgTable(
  "task_comments",
  {
    id: uuid("id").primaryKey(),
    taskId: uuid("task_id")
      .notNull()
      .references(() => tasks.id),
    authorType: text("author_type").notNull(),
    authorId: text("author_id").notNull(),
    body: text("body").notNull(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("task_comments_task").on(t.taskId, t.createdAt),
    check(
      "task_comments_author_type_check",
      sql`${t.authorType} IN ('user','agent','system')`,
    ),
  ],
);
