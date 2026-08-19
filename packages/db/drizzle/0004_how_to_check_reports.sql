ALTER TABLE "tasks" ADD COLUMN "how_to_check" text DEFAULT '' NOT NULL;
--> statement-breakpoint
CREATE TABLE "project_reports" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"title" text NOT NULL,
	"body_md" text NOT NULL,
	"snapshot" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"created_by_type" text NOT NULL,
	"created_by_id" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "project_reviews" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"title" text NOT NULL,
	"body_md" text NOT NULL,
	"source" text DEFAULT 'imported' NOT NULL,
	"source_path" text,
	"status" text DEFAULT 'needs_review' NOT NULL,
	"created_by_type" text NOT NULL,
	"created_by_id" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "project_reviews_status_check" CHECK ("project_reviews"."status" IN ('needs_review','reviewed'))
);
--> statement-breakpoint
ALTER TABLE "project_reports" ADD CONSTRAINT "project_reports_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;
--> statement-breakpoint
ALTER TABLE "project_reviews" ADD CONSTRAINT "project_reviews_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;
--> statement-breakpoint
CREATE INDEX "project_reports_project_time" ON "project_reports" USING btree ("project_id","created_at" DESC);
--> statement-breakpoint
CREATE INDEX "project_reviews_project_time" ON "project_reviews" USING btree ("project_id","created_at" DESC);
