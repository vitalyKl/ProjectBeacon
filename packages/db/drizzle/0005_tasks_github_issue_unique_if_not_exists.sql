CREATE UNIQUE INDEX IF NOT EXISTS "tasks_project_github_issue_id_unique" ON "tasks" USING btree ("project_id","github_issue_id") WHERE "tasks"."github_issue_id" IS NOT NULL;
--> statement-breakpoint
