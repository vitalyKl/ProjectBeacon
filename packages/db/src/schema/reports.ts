import { sql } from "drizzle-orm";
import { check, index, jsonb, pgTable, text, uuid } from "drizzle-orm/pg-core";
import { timestamptz } from "./common.js";
import { projects } from "./projects.js";

export const projectReports = pgTable(
  "project_reports",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    title: text("title").notNull(),
    bodyMd: text("body_md").notNull(),
    snapshot: jsonb("snapshot").notNull().default(sql`'{}'::jsonb`),
    createdByType: text("created_by_type").notNull(),
    createdById: text("created_by_id").notNull(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [index("project_reports_project_time").on(t.projectId, t.createdAt.desc())],
);

export const projectReviews = pgTable(
  "project_reviews",
  {
    id: uuid("id").primaryKey(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    title: text("title").notNull(),
    bodyMd: text("body_md").notNull(),
    source: text("source").notNull().default("imported"),
    sourcePath: text("source_path"),
    status: text("status").notNull().default("needs_review"),
    createdByType: text("created_by_type").notNull(),
    createdById: text("created_by_id").notNull(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [
    index("project_reviews_project_time").on(t.projectId, t.createdAt.desc()),
    check("project_reviews_status_check", sql`${t.status} IN ('needs_review','reviewed')`),
  ],
);
