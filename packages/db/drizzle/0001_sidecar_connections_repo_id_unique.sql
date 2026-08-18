DELETE FROM "sidecar_connections" AS older
USING "sidecar_connections" AS newer
WHERE older."repo_id" = newer."repo_id"
	AND (
		older."last_seen_at" < newer."last_seen_at"
		OR (older."last_seen_at" = newer."last_seen_at" AND older."id" < newer."id")
	);
--> statement-breakpoint
ALTER TABLE "sidecar_connections" ADD CONSTRAINT "sidecar_connections_repo_id_unique" UNIQUE("repo_id");