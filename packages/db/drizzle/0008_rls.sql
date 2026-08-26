-- Enable RLS on all multi-tenant tables.
-- Policies are keyed off `app.actor_org_id`, set by the API middleware
-- from the authenticated actor's org. RLS is the backstop — app-layer
-- authorisation still runs, but a missing WHERE clause cannot leak data
-- across tenants.

CREATE FUNCTION beacon.current_actor_org_id() RETURNS uuid AS $$
  SELECT NULLIF(current_setting('app.actor_org_id', true), '')::uuid;
$$ LANGUAGE sql STABLE;

-- ── Org-level tables (direct org_id) ────────────────────────────────────

ALTER TABLE "orgs" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "orgs_org_scope" ON "orgs"
  USING ("orgs"."id" = beacon.current_actor_org_id());

ALTER TABLE "org_members" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "org_members_org_scope" ON "org_members"
  USING ("org_members"."org_id" = beacon.current_actor_org_id());

ALTER TABLE "org_invites" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "org_invites_org_scope" ON "org_invites"
  USING ("org_invites"."org_id" = beacon.current_actor_org_id());

ALTER TABLE "github_installations" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "github_installations_org_scope" ON "github_installations"
  USING ("github_installations"."org_id" = beacon.current_actor_org_id());

-- ── Project-level tables (via projects.org_id) ──────────────────────────

ALTER TABLE "projects" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "projects_org_scope" ON "projects"
  USING ("projects"."org_id" = beacon.current_actor_org_id());

ALTER TABLE "project_repos" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_repos_org_scope" ON "project_repos"
  USING (
    "project_repos"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "project_members" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_members_org_scope" ON "project_members"
  USING (
    "project_members"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "project_invites" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_invites_org_scope" ON "project_invites"
  USING (
    "project_invites"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "code_owners" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "code_owners_org_scope" ON "code_owners"
  USING (
    "code_owners"."repo_id" IN (
      SELECT "project_repos"."id" FROM "project_repos"
      JOIN "projects" ON "projects"."id" = "project_repos"."project_id"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "milestones" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "milestones_org_scope" ON "milestones"
  USING (
    "milestones"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "tasks" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "tasks_org_scope" ON "tasks"
  USING (
    "tasks"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "task_dependencies" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "task_dependencies_org_scope" ON "task_dependencies"
  USING (
    "task_dependencies"."from_task_id" IN (
      SELECT "tasks"."id" FROM "tasks"
      WHERE "tasks"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "task_comments" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "task_comments_org_scope" ON "task_comments"
  USING (
    "task_comments"."task_id" IN (
      SELECT "tasks"."id" FROM "tasks"
      WHERE "tasks"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "context_nodes" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "context_nodes_org_scope" ON "context_nodes"
  USING (
    "context_nodes"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "context_revisions" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "context_revisions_org_scope" ON "context_revisions"
  USING (
    "context_revisions"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "labels" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "labels_org_scope" ON "labels"
  USING (
    "labels"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "label_paths" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "label_paths_org_scope" ON "label_paths"
  USING (
    "label_paths"."label_id" IN (
      SELECT "labels"."id" FROM "labels"
      WHERE "labels"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "task_labels" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "task_labels_org_scope" ON "task_labels"
  USING (
    "task_labels"."task_id" IN (
      SELECT "tasks"."id" FROM "tasks"
      WHERE "tasks"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "decisions" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "decisions_org_scope" ON "decisions"
  USING (
    "decisions"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "decision_paths" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "decision_paths_org_scope" ON "decision_paths"
  USING (
    "decision_paths"."decision_id" IN (
      SELECT "decisions"."id" FROM "decisions"
      WHERE "decisions"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "decision_tasks" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "decision_tasks_org_scope" ON "decision_tasks"
  USING (
    "decision_tasks"."decision_id" IN (
      SELECT "decisions"."id" FROM "decisions"
      WHERE "decisions"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "constraints" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "constraints_org_scope" ON "constraints"
  USING (
    "constraints"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "project_reports" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_reports_org_scope" ON "project_reports"
  USING (
    "project_reports"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "project_reviews" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_reviews_org_scope" ON "project_reviews"
  USING (
    "project_reviews"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "project_eval_metrics" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "project_eval_metrics_org_scope" ON "project_eval_metrics"
  USING (
    "project_eval_metrics"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "agent_sessions" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "agent_sessions_org_scope" ON "agent_sessions"
  USING (
    "agent_sessions"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "handoffs" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "handoffs_org_scope" ON "handoffs"
  USING (
    "handoffs"."session_id" IN (
      SELECT "agent_sessions"."id" FROM "agent_sessions"
      WHERE "agent_sessions"."project_id" IN (
        SELECT "projects"."id" FROM "projects"
        WHERE "projects"."org_id" = beacon.current_actor_org_id()
      )
    )
  );

ALTER TABLE "activity_events" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "activity_events_org_scope" ON "activity_events"
  USING (
    "activity_events"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "approval_requests" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "approval_requests_org_scope" ON "approval_requests"
  USING (
    "approval_requests"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "api_tokens" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "api_tokens_org_scope" ON "api_tokens"
  USING (
    "api_tokens"."project_id" IN (
      SELECT "projects"."id" FROM "projects"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "sidecar_connections" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "sidecar_connections_org_scope" ON "sidecar_connections"
  USING (
    "sidecar_connections"."repo_id" IN (
      SELECT "project_repos"."id" FROM "project_repos"
      JOIN "projects" ON "projects"."id" = "project_repos"."project_id"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

-- ── Non-multi-tenant tables (no RLS needed) ─────────────────────────────

-- user_sessions: user-scoped, not org-scoped. The user is single-tenant.
-- users: global directory, not scoped.
-- rate_buckets: global rate limiting, not scoped.
-- idempotency_keys: global idempotency store, keyed by actor (user/token).
-- github_sync_state: repo-scoped (resolved via project_repos → projects).
-- github_clone_invalidations: repo-scoped (resolved via project_repos → projects).

ALTER TABLE "github_sync_state" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "github_sync_state_org_scope" ON "github_sync_state"
  USING (
    "github_sync_state"."repo_id" IN (
      SELECT "project_repos"."id" FROM "project_repos"
      JOIN "projects" ON "projects"."id" = "project_repos"."project_id"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );

ALTER TABLE "github_clone_invalidations" ENABLE ROW LEVEL SECURITY;
CREATE POLICY "github_clone_invalidations_org_scope" ON "github_clone_invalidations"
  USING (
    "github_clone_invalidations"."repo_id" IN (
      SELECT "project_repos"."id" FROM "project_repos"
      JOIN "projects" ON "projects"."id" = "project_repos"."project_id"
      WHERE "projects"."org_id" = beacon.current_actor_org_id()
    )
  );
