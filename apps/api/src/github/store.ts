import type { ProjectRepoRecord } from "../context/types.js";
import type { TaskRecord } from "../roadmap/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "./types.js";

export interface GithubStore {
  findProjectRepoByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord | undefined>;
  listProjectReposByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord[]>;
  findTaskByGithubIssueId(
    projectId: string,
    githubIssueId: bigint,
  ): Promise<TaskRecord | undefined>;
  findGithubInstallationByInstallationId(
    installationId: bigint,
  ): Promise<GithubInstallationRecord | undefined>;
  upsertGithubInstallation(row: GithubInstallationRecord): Promise<GithubInstallationRecord>;
  findGithubSyncState(repoId: string): Promise<GithubSyncStateRecord | undefined>;
  upsertGithubSyncState(row: GithubSyncStateRecord): Promise<GithubSyncStateRecord>;
}
