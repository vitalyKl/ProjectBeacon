import type { TaskRecord } from "../../roadmap/types.js";
import type { ProjectRepoRecord } from "../../context/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../../github/types.js";
import { cloneTask, cloneProjectRepo, cloneGithubInstallation, cloneGithubSyncState } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryGithub<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryGithub extends Base {
  async findProjectRepoByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord | undefined> {
    const [repo] = await this.listProjectReposByGithubRepoId(githubRepoId);
    return repo;
  }

  async listProjectReposByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord[]> {
    const result: ProjectRepoRecord[] = [];
    for (const repo of this.projectRepos.values()) {
      if (repo.githubRepoId === githubRepoId) {
        result.push(cloneProjectRepo(repo));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async findTaskByGithubIssueId(
    projectId: string,
    githubIssueId: bigint,
  ): Promise<TaskRecord | undefined> {
    for (const task of this.tasks.values()) {
      if (task.projectId === projectId && task.githubIssueId === githubIssueId) {
        return cloneTask(task);
      }
    }
    return undefined;
  }

  async findGithubInstallationByInstallationId(
    installationId: bigint,
  ): Promise<GithubInstallationRecord | undefined> {
    for (const row of this.githubInstallations.values()) {
      if (row.installationId === installationId) {
        return cloneGithubInstallation(row);
      }
    }
    return undefined;
  }

  async upsertGithubInstallation(row: GithubInstallationRecord): Promise<GithubInstallationRecord> {
    return this.enqueueWrite(() => {
      for (const existing of this.githubInstallations.values()) {
        if (existing.installationId === row.installationId) {
          existing.orgId = row.orgId;
          existing.accountLogin = row.accountLogin;
          return cloneGithubInstallation(existing);
        }
      }
      this.githubInstallations.set(row.id, cloneGithubInstallation(row));
      return cloneGithubInstallation(row);
    });
  }

  async findGithubSyncState(repoId: string): Promise<GithubSyncStateRecord | undefined> {
    const row = this.githubSyncState.get(repoId);
    return row ? cloneGithubSyncState(row) : undefined;
  }

  async upsertGithubSyncState(row: GithubSyncStateRecord): Promise<GithubSyncStateRecord> {
    return this.enqueueWrite(() => {
      this.githubSyncState.set(row.repoId, cloneGithubSyncState(row));
      return cloneGithubSyncState(row);
    });
  }
  };
}
