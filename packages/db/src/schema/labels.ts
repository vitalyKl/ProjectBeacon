import { sql } from "drizzle-orm";
import { check, index, pgTable, primaryKey, text, uniqueIndex, uuid } from "drizzle-orm/pg-core";

import { timestamptz } from "./common.js";
import { projectRepos, projects } from "./projects.js";
import { tasks } from "./tasks.js";

export const labels = pgTable(
  "labels",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    slug: text("slug").notNull(),
    name: text("name").notNull(),
    description: text("description").notNull().default(""),
    color: text("color"),
    status: text("status").notNull().default("active"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    uniqueIndex("labels_project_slug").on(t.projectId, t.slug),
    index("labels_project_status").on(t.projectId, t.status),
    check("labels_status_check", sql`${t.status} IN ('proposed','active')`),
  ],
);

export const labelPaths = pgTable(
  "label_paths",
  {
    labelId: uuid("label_id")
      .notNull()
      .references(() => labels.id, { onDelete: "cascade" }),
    repoId: uuid("repo_id")
      .notNull()
      .references(() => projectRepos.id),
    path: text("path").notNull(),
  },
  (t) => [
    primaryKey({ columns: [t.labelId, t.repoId, t.path] }),
    index("label_paths_repo").on(t.repoId, t.path),
  ],
);

export const taskLabels = pgTable(
  "task_labels",
  {
    taskId: uuid("task_id")
      .notNull()
      .references(() => tasks.id, { onDelete: "cascade" }),
    labelId: uuid("label_id")
      .notNull()
      .references(() => labels.id, { onDelete: "cascade" }),
  },
  (t) => [
    primaryKey({ columns: [t.taskId, t.labelId] }),
    index("task_labels_label").on(t.labelId),
  ],
);
