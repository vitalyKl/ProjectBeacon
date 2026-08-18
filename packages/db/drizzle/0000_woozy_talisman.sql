CREATE TABLE "context_nodes" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"repo_id" uuid,
	"task_id" uuid,
	"scope_type" text NOT NULL,
	"path" text DEFAULT '' NOT NULL,
	"sections" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"sections_text" text DEFAULT '' NOT NULL,
	"source" text NOT NULL,
	"source_path" text,
	"review_state" text DEFAULT 'reviewed' NOT NULL,
	"updated_by_type" text NOT NULL,
	"updated_by_id" text NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "context_nodes_scope_type_check" CHECK ("context_nodes"."scope_type" IN ('project','repo','path','task')),
	CONSTRAINT "context_nodes_review_state_check" CHECK ("context_nodes"."review_state" IN ('reviewed','needs_review'))
);
--> statement-breakpoint
CREATE TABLE "context_revisions" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"compiled_hash" text NOT NULL,
	"compiler_version" text NOT NULL,
	"target" jsonb NOT NULL,
	"brief_markdown" text NOT NULL,
	"brief_json" jsonb NOT NULL,
	"token_estimate" integer NOT NULL,
	"source_node_ids" uuid[] NOT NULL,
	"session_id" uuid,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "constraints" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"kind" text NOT NULL,
	"body" text NOT NULL,
	"scope_path" text DEFAULT '' NOT NULL,
	"status" text DEFAULT 'proposed' NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "constraints_kind_check" CHECK ("constraints"."kind" IN ('must','must_not','security','compliance')),
	CONSTRAINT "constraints_status_check" CHECK ("constraints"."status" IN ('proposed','active','rejected'))
);
--> statement-breakpoint
CREATE TABLE "decision_paths" (
	"decision_id" uuid NOT NULL,
	"repo_id" uuid NOT NULL,
	"path" text NOT NULL,
	CONSTRAINT "decision_paths_decision_id_repo_id_path_pk" PRIMARY KEY("decision_id","repo_id","path")
);
--> statement-breakpoint
CREATE TABLE "decision_tasks" (
	"decision_id" uuid NOT NULL,
	"task_id" uuid NOT NULL,
	CONSTRAINT "decision_tasks_decision_id_task_id_pk" PRIMARY KEY("decision_id","task_id")
);
--> statement-breakpoint
CREATE TABLE "decisions" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"title" text NOT NULL,
	"status" text NOT NULL,
	"context" text NOT NULL,
	"decision" text NOT NULL,
	"consequences" text DEFAULT '' NOT NULL,
	"created_by_type" text NOT NULL,
	"created_by_id" text NOT NULL,
	"superseded_by" uuid,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "decisions_status_check" CHECK ("decisions"."status" IN ('proposed','accepted','superseded','deprecated'))
);
--> statement-breakpoint
CREATE TABLE "org_invites" (
	"id" uuid PRIMARY KEY NOT NULL,
	"org_id" uuid NOT NULL,
	"email" text,
	"github_login" text,
	"role" text NOT NULL,
	"expires_at" timestamp with time zone NOT NULL,
	"accepted_at" timestamp with time zone,
	CONSTRAINT "org_invites_role_check" CHECK ("org_invites"."role" IN ('admin','member'))
);
--> statement-breakpoint
CREATE TABLE "org_members" (
	"org_id" uuid NOT NULL,
	"user_id" uuid NOT NULL,
	"role" text NOT NULL,
	CONSTRAINT "org_members_org_id_user_id_pk" PRIMARY KEY("org_id","user_id"),
	CONSTRAINT "org_members_role_check" CHECK ("org_members"."role" IN ('owner','admin','member'))
);
--> statement-breakpoint
CREATE TABLE "orgs" (
	"id" uuid PRIMARY KEY NOT NULL,
	"slug" text NOT NULL,
	"name" text NOT NULL,
	"kind" text DEFAULT 'team' NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "orgs_slug_unique" UNIQUE("slug"),
	CONSTRAINT "orgs_kind_check" CHECK ("orgs"."kind" IN ('personal','team'))
);
--> statement-breakpoint
CREATE TABLE "user_sessions" (
	"id" uuid PRIMARY KEY NOT NULL,
	"user_id" uuid NOT NULL,
	"token_hash" "bytea" NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	"expires_at" timestamp with time zone NOT NULL,
	"revoked_at" timestamp with time zone,
	"user_agent" text,
	"ip" "inet",
	CONSTRAINT "user_sessions_token_hash_unique" UNIQUE("token_hash")
);
--> statement-breakpoint
CREATE TABLE "users" (
	"id" uuid PRIMARY KEY NOT NULL,
	"github_id" bigint,
	"login" text NOT NULL,
	"email" text,
	"name" text,
	"avatar_url" text,
	"password_hash" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "users_github_id_unique" UNIQUE("github_id"),
	CONSTRAINT "users_login_unique" UNIQUE("login")
);
--> statement-breakpoint
CREATE TABLE "code_owners" (
	"id" uuid PRIMARY KEY NOT NULL,
	"repo_id" uuid NOT NULL,
	"path_pattern" text NOT NULL,
	"owners" text[] NOT NULL,
	"source" text DEFAULT 'codeowners' NOT NULL,
	CONSTRAINT "code_owners_repo_id_path_pattern_unique" UNIQUE("repo_id","path_pattern")
);
--> statement-breakpoint
CREATE TABLE "project_invites" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"email" text,
	"github_login" text,
	"role" text NOT NULL,
	"invited_by" uuid NOT NULL,
	"expires_at" timestamp with time zone NOT NULL,
	"accepted_at" timestamp with time zone,
	CONSTRAINT "project_invites_role_check" CHECK ("project_invites"."role" IN ('admin','write','read'))
);
--> statement-breakpoint
CREATE TABLE "project_members" (
	"project_id" uuid NOT NULL,
	"user_id" uuid NOT NULL,
	"role" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "project_members_project_id_user_id_pk" PRIMARY KEY("project_id","user_id"),
	CONSTRAINT "project_members_role_check" CHECK ("project_members"."role" IN ('admin','write','read'))
);
--> statement-breakpoint
CREATE TABLE "project_repos" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"provider" text NOT NULL,
	"remote_url" text,
	"default_branch" text DEFAULT 'main' NOT NULL,
	"github_repo_id" bigint,
	"installation_id" bigint,
	"local_root_hint" text,
	"index_mode" text DEFAULT 'sidecar' NOT NULL,
	"last_indexed_sha" text,
	"last_indexed_at" timestamp with time zone,
	CONSTRAINT "project_repos_project_id_github_repo_id_unique" UNIQUE("project_id","github_repo_id"),
	CONSTRAINT "project_repos_provider_check" CHECK ("project_repos"."provider" IN ('github','local')),
	CONSTRAINT "project_repos_index_mode_check" CHECK ("project_repos"."index_mode" IN ('sidecar','bind_mount','hosted_clone','both'))
);
--> statement-breakpoint
CREATE TABLE "projects" (
	"id" uuid PRIMARY KEY NOT NULL,
	"org_id" uuid NOT NULL,
	"slug" text NOT NULL,
	"name" text NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"visibility" text DEFAULT 'private' NOT NULL,
	"default_repo_id" uuid,
	"settings" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"deleted_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "projects_org_id_slug_unique" UNIQUE("org_id","slug"),
	CONSTRAINT "projects_visibility_check" CHECK ("projects"."visibility" IN ('private'))
);
--> statement-breakpoint
CREATE TABLE "activity_events" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"object_type" text NOT NULL,
	"object_id" text NOT NULL,
	"actor_type" text NOT NULL,
	"actor_id" text NOT NULL,
	"verb" text NOT NULL,
	"payload" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "agent_sessions" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"task_id" uuid,
	"token_id" uuid,
	"agent_name" text NOT NULL,
	"agent_host" text NOT NULL,
	"status" text NOT NULL,
	"context_revision_id" uuid,
	"started_at" timestamp with time zone DEFAULT now() NOT NULL,
	"finished_at" timestamp with time zone,
	"lock_expires_at" timestamp with time zone,
	"last_heartbeat_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "agent_sessions_status_check" CHECK ("agent_sessions"."status" IN ('active','paused','finished','abandoned'))
);
--> statement-breakpoint
CREATE TABLE "api_tokens" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"name" text NOT NULL,
	"token_hash" "bytea" NOT NULL,
	"prefix" text NOT NULL,
	"scopes" text[] NOT NULL,
	"created_by" uuid,
	"last_used_at" timestamp with time zone,
	"expires_at" timestamp with time zone,
	"revoked_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "api_tokens_token_hash_unique" UNIQUE("token_hash")
);
--> statement-breakpoint
CREATE TABLE "approval_requests" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"session_id" uuid,
	"action" text NOT NULL,
	"payload" jsonb NOT NULL,
	"status" text NOT NULL,
	"requested_at" timestamp with time zone DEFAULT now() NOT NULL,
	"resolved_at" timestamp with time zone,
	"resolved_by" uuid,
	CONSTRAINT "approval_requests_status_check" CHECK ("approval_requests"."status" IN ('pending','approved','denied','expired'))
);
--> statement-breakpoint
CREATE TABLE "github_installations" (
	"id" uuid PRIMARY KEY NOT NULL,
	"org_id" uuid NOT NULL,
	"installation_id" bigint NOT NULL,
	"account_login" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "github_installations_installation_id_unique" UNIQUE("installation_id")
);
--> statement-breakpoint
CREATE TABLE "github_sync_state" (
	"repo_id" uuid PRIMARY KEY NOT NULL,
	"last_cursor" text,
	"last_synced_at" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "handoffs" (
	"id" uuid PRIMARY KEY NOT NULL,
	"session_id" uuid NOT NULL,
	"task_id" uuid,
	"summary" text NOT NULL,
	"next_steps" text DEFAULT '' NOT NULL,
	"files_touched" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"open_questions" text[] DEFAULT '{}' NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "idempotency_keys" (
	"actor_type" text NOT NULL,
	"actor_id" uuid NOT NULL,
	"key" text NOT NULL,
	"response" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "idempotency_keys_actor_type_actor_id_key_pk" PRIMARY KEY("actor_type","actor_id","key"),
	CONSTRAINT "idempotency_keys_actor_type_check" CHECK ("idempotency_keys"."actor_type" IN ('token','user'))
);
--> statement-breakpoint
CREATE TABLE "rate_buckets" (
	"bucket_key" text PRIMARY KEY NOT NULL,
	"window_start" timestamp with time zone NOT NULL,
	"count" integer NOT NULL,
	"bytes" bigint DEFAULT 0 NOT NULL
);
--> statement-breakpoint
CREATE TABLE "sidecar_connections" (
	"id" uuid PRIMARY KEY NOT NULL,
	"repo_id" uuid NOT NULL,
	"token_id" uuid NOT NULL,
	"connected_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "sidecar_connections_repo_id_unique" UNIQUE("repo_id")
);
--> statement-breakpoint
CREATE TABLE "milestones" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"title" text NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"status" text DEFAULT 'open' NOT NULL,
	"target_date" date,
	"sort_order" integer DEFAULT 0 NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "milestones_status_check" CHECK ("milestones"."status" IN ('open','closed'))
);
--> statement-breakpoint
CREATE TABLE "task_comments" (
	"id" uuid PRIMARY KEY NOT NULL,
	"task_id" uuid NOT NULL,
	"author_type" text NOT NULL,
	"author_id" text NOT NULL,
	"body" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "task_comments_author_type_check" CHECK ("task_comments"."author_type" IN ('user','agent','system'))
);
--> statement-breakpoint
CREATE TABLE "task_dependencies" (
	"from_task_id" uuid NOT NULL,
	"to_task_id" uuid NOT NULL,
	"type" text NOT NULL,
	CONSTRAINT "task_dependencies_from_task_id_to_task_id_type_pk" PRIMARY KEY("from_task_id","to_task_id","type"),
	CONSTRAINT "task_dependencies_type_check" CHECK ("task_dependencies"."type" IN ('blocks','relates')),
	CONSTRAINT "task_dependencies_no_self_check" CHECK ("task_dependencies"."from_task_id" <> "task_dependencies"."to_task_id")
);
--> statement-breakpoint
CREATE TABLE "tasks" (
	"id" uuid PRIMARY KEY NOT NULL,
	"project_id" uuid NOT NULL,
	"milestone_id" uuid,
	"parent_id" uuid,
	"title" text NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"status" text DEFAULT 'backlog' NOT NULL,
	"priority" integer DEFAULT 0 NOT NULL,
	"type" text DEFAULT 'task' NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"assignee_user_id" uuid,
	"assignee_agent_name" text,
	"agent_brief" text DEFAULT '' NOT NULL,
	"linked_paths" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"github_issue_id" bigint,
	"locked_by_session_id" uuid,
	"lock_expires_at" timestamp with time zone,
	"deleted_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "tasks_status_check" CHECK ("tasks"."status" IN (
        'backlog','ready','in_progress','blocked',
        'in_review','done','canceled'
      )),
	CONSTRAINT "tasks_type_check" CHECK ("tasks"."type" IN ('epic','story','task','bug'))
);
--> statement-breakpoint
ALTER TABLE "context_nodes" ADD CONSTRAINT "context_nodes_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "context_nodes" ADD CONSTRAINT "context_nodes_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "context_nodes" ADD CONSTRAINT "context_nodes_task_fk" FOREIGN KEY ("task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "context_revisions" ADD CONSTRAINT "context_revisions_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "constraints" ADD CONSTRAINT "constraints_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decision_paths" ADD CONSTRAINT "decision_paths_decision_id_decisions_id_fk" FOREIGN KEY ("decision_id") REFERENCES "public"."decisions"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decision_paths" ADD CONSTRAINT "decision_paths_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decision_tasks" ADD CONSTRAINT "decision_tasks_decision_id_decisions_id_fk" FOREIGN KEY ("decision_id") REFERENCES "public"."decisions"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decision_tasks" ADD CONSTRAINT "decision_tasks_task_id_tasks_id_fk" FOREIGN KEY ("task_id") REFERENCES "public"."tasks"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decisions" ADD CONSTRAINT "decisions_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "decisions" ADD CONSTRAINT "decisions_superseded_by_decisions_id_fk" FOREIGN KEY ("superseded_by") REFERENCES "public"."decisions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "org_invites" ADD CONSTRAINT "org_invites_org_id_orgs_id_fk" FOREIGN KEY ("org_id") REFERENCES "public"."orgs"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "org_members" ADD CONSTRAINT "org_members_org_id_orgs_id_fk" FOREIGN KEY ("org_id") REFERENCES "public"."orgs"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "org_members" ADD CONSTRAINT "org_members_user_id_users_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "user_sessions" ADD CONSTRAINT "user_sessions_user_id_users_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "code_owners" ADD CONSTRAINT "code_owners_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "project_invites" ADD CONSTRAINT "project_invites_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "project_invites" ADD CONSTRAINT "project_invites_invited_by_users_id_fk" FOREIGN KEY ("invited_by") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "project_members" ADD CONSTRAINT "project_members_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "project_members" ADD CONSTRAINT "project_members_user_id_users_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "project_repos" ADD CONSTRAINT "project_repos_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "projects" ADD CONSTRAINT "projects_org_id_orgs_id_fk" FOREIGN KEY ("org_id") REFERENCES "public"."orgs"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "projects" ADD CONSTRAINT "projects_default_repo_fk" FOREIGN KEY ("default_repo_id") REFERENCES "public"."project_repos"("id") ON DELETE set null ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "activity_events" ADD CONSTRAINT "activity_events_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "agent_sessions" ADD CONSTRAINT "agent_sessions_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "agent_sessions" ADD CONSTRAINT "agent_sessions_task_id_tasks_id_fk" FOREIGN KEY ("task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "agent_sessions" ADD CONSTRAINT "agent_sessions_token_id_api_tokens_id_fk" FOREIGN KEY ("token_id") REFERENCES "public"."api_tokens"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "agent_sessions" ADD CONSTRAINT "agent_sessions_context_revision_id_context_revisions_id_fk" FOREIGN KEY ("context_revision_id") REFERENCES "public"."context_revisions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "api_tokens" ADD CONSTRAINT "api_tokens_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "api_tokens" ADD CONSTRAINT "api_tokens_created_by_users_id_fk" FOREIGN KEY ("created_by") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "approval_requests" ADD CONSTRAINT "approval_requests_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "approval_requests" ADD CONSTRAINT "approval_requests_session_id_agent_sessions_id_fk" FOREIGN KEY ("session_id") REFERENCES "public"."agent_sessions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "approval_requests" ADD CONSTRAINT "approval_requests_resolved_by_users_id_fk" FOREIGN KEY ("resolved_by") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "github_installations" ADD CONSTRAINT "github_installations_org_id_orgs_id_fk" FOREIGN KEY ("org_id") REFERENCES "public"."orgs"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "github_sync_state" ADD CONSTRAINT "github_sync_state_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "handoffs" ADD CONSTRAINT "handoffs_session_id_agent_sessions_id_fk" FOREIGN KEY ("session_id") REFERENCES "public"."agent_sessions"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "handoffs" ADD CONSTRAINT "handoffs_task_id_tasks_id_fk" FOREIGN KEY ("task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "sidecar_connections" ADD CONSTRAINT "sidecar_connections_repo_id_project_repos_id_fk" FOREIGN KEY ("repo_id") REFERENCES "public"."project_repos"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "sidecar_connections" ADD CONSTRAINT "sidecar_connections_token_id_api_tokens_id_fk" FOREIGN KEY ("token_id") REFERENCES "public"."api_tokens"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "milestones" ADD CONSTRAINT "milestones_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "task_comments" ADD CONSTRAINT "task_comments_task_id_tasks_id_fk" FOREIGN KEY ("task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "task_dependencies" ADD CONSTRAINT "task_dependencies_from_task_id_tasks_id_fk" FOREIGN KEY ("from_task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "task_dependencies" ADD CONSTRAINT "task_dependencies_to_task_id_tasks_id_fk" FOREIGN KEY ("to_task_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "tasks" ADD CONSTRAINT "tasks_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "public"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "tasks" ADD CONSTRAINT "tasks_milestone_id_milestones_id_fk" FOREIGN KEY ("milestone_id") REFERENCES "public"."milestones"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "tasks" ADD CONSTRAINT "tasks_parent_id_tasks_id_fk" FOREIGN KEY ("parent_id") REFERENCES "public"."tasks"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "tasks" ADD CONSTRAINT "tasks_assignee_user_id_users_id_fk" FOREIGN KEY ("assignee_user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "context_nodes_lookup" ON "context_nodes" USING btree ("project_id","scope_type","path");--> statement-breakpoint
CREATE UNIQUE INDEX "context_nodes_unique_scope" ON "context_nodes" USING btree ("project_id","scope_type",COALESCE("repo_id", '00000000-0000-0000-0000-000000000000'),"path",COALESCE("task_id", '00000000-0000-0000-0000-000000000000'));--> statement-breakpoint
CREATE INDEX "context_nodes_fts" ON "context_nodes" USING gin (to_tsvector('simple', coalesce("sections_text", '')));--> statement-breakpoint
CREATE INDEX "context_revisions_project_time" ON "context_revisions" USING btree ("project_id","created_at" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "constraints_project" ON "constraints" USING btree ("project_id","status");--> statement-breakpoint
CREATE INDEX "decision_paths_lookup" ON "decision_paths" USING btree ("repo_id","path");--> statement-breakpoint
CREATE INDEX "decision_tasks_task" ON "decision_tasks" USING btree ("task_id");--> statement-breakpoint
CREATE INDEX "decisions_project" ON "decisions" USING btree ("project_id","status");--> statement-breakpoint
CREATE INDEX "user_sessions_user" ON "user_sessions" USING btree ("user_id") WHERE "user_sessions"."revoked_at" IS NULL;--> statement-breakpoint
CREATE UNIQUE INDEX "project_repos_local_root" ON "project_repos" USING btree ("project_id","local_root_hint") WHERE "project_repos"."provider" = 'local' AND "project_repos"."local_root_hint" IS NOT NULL;--> statement-breakpoint
CREATE INDEX "activity_project_time" ON "activity_events" USING btree ("project_id","created_at" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "activity_object" ON "activity_events" USING btree ("project_id","object_type","object_id","created_at" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "agent_sessions_project" ON "agent_sessions" USING btree ("project_id","started_at" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "agent_sessions_active" ON "agent_sessions" USING btree ("task_id") WHERE "agent_sessions"."status" = 'active';--> statement-breakpoint
CREATE INDEX "handoffs_task" ON "handoffs" USING btree ("task_id","created_at" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "milestones_project" ON "milestones" USING btree ("project_id","sort_order");--> statement-breakpoint
CREATE INDEX "task_comments_task" ON "task_comments" USING btree ("task_id","created_at");--> statement-breakpoint
CREATE INDEX "tasks_project_status" ON "tasks" USING btree ("project_id","status") WHERE "tasks"."deleted_at" IS NULL;--> statement-breakpoint
CREATE INDEX "tasks_project_milestone" ON "tasks" USING btree ("project_id","milestone_id") WHERE "tasks"."deleted_at" IS NULL;--> statement-breakpoint
CREATE INDEX "tasks_updated" ON "tasks" USING btree ("project_id","updated_at" DESC NULLS LAST,"id" DESC NULLS LAST);--> statement-breakpoint
CREATE INDEX "tasks_title_fts" ON "tasks" USING gin (to_tsvector('simple', "title" || ' ' || "description"));
--> statement-breakpoint
CREATE OR REPLACE FUNCTION context_nodes_sections_text()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  SELECT coalesce(string_agg(coalesce(x.title, '') || E'\n' || coalesce(x.body_md, ''), E'\n\n'), '')
    INTO NEW.sections_text
    FROM jsonb_to_recordset(coalesce(NEW.sections, '[]'::jsonb)) AS x(title text, body_md text);
  RETURN NEW;
END;
$$;
--> statement-breakpoint
CREATE TRIGGER context_nodes_sections_text
BEFORE INSERT OR UPDATE OF sections ON context_nodes
FOR EACH ROW
EXECUTE FUNCTION context_nodes_sections_text();
