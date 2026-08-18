CREATE TABLE "github_clone_invalidations" (
	"id" uuid PRIMARY KEY NOT NULL,
	"repo_id" uuid NOT NULL,
	"sha" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"consumed_at" timestamp with time zone
);
--> statement-breakpoint
ALTER TABLE "github_clone_invalidations" ADD CONSTRAINT "github_clone_invalidations_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE cascade ON UPDATE no action;
--> statement-breakpoint
CREATE INDEX "github_clone_invalidations_repo_pending" ON "github_clone_invalidations" USING btree ("repo_id","created_at");
