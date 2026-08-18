import type { GithubSyncStateRecord } from "./types.js";

export function presentGithubSyncState(row: GithubSyncStateRecord) {
  return {
    repo_id: row.repoId,
    last_cursor: row.lastCursor,
    last_synced_at: row.lastSyncedAt ? row.lastSyncedAt.toISOString() : null,
  };
}

export function presentGithubLink(task: {
  id: string;
  githubIssueId: bigint | null;
  version: number;
}) {
  return {
    task_id: task.id,
    github_issue_id: task.githubIssueId === null ? null : task.githubIssueId.toString(),
    version: task.version,
  };
}
