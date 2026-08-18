import type { ProjectRepoRecord } from "../context/types.js";

export function presentProjectRepo(
  repo: ProjectRepoRecord,
  extras: { sidecarConnected?: boolean; workerIndexConnected?: boolean } = {},
) {
  return {
    id: repo.id,
    project_id: repo.projectId,
    provider: repo.provider,
    remote_url: repo.remoteUrl,
    default_branch: repo.defaultBranch,
    github_repo_id: repo.githubRepoId === null ? null : repo.githubRepoId.toString(),
    installation_id: repo.installationId === null ? null : repo.installationId.toString(),
    local_root_hint: repo.localRootHint,
    index_mode: repo.indexMode,
    sidecar_connected: extras.sidecarConnected ?? false,
    worker_index_connected: extras.workerIndexConnected ?? false,
    last_indexed_at: repo.lastIndexedAt ? repo.lastIndexedAt.toISOString() : null,
    last_indexed_sha: repo.lastIndexedSha,
  };
}
