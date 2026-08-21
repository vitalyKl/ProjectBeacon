import { projectRepos, tasks, githubInstallations, githubSyncState } from "@beacon/db";
import { and, asc, eq } from "drizzle-orm";
import type { TaskRecord } from "../../roadmap/types.js";
import type { ProjectRepoRecord } from "../../context/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../../github/types.js";
import { toTask, toProjectRepo, toGithubInstallation, toGithubSyncState } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { GithubStore } from "../../github/store.js";

export function withDbGithub<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<GithubStore> {
  return class DbGithub extends Base {
  async findProjectRepoByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord | undefined> {
    const [repo] = await this.listProjectReposByGithubRepoId(githubRepoId);
    return repo;
  }

  async listProjectReposByGithubRepoId(githubRepoId: bigint): Promise<ProjectRepoRecord[]> {
    const rows = await this.db
      .select()
      .from(projectRepos)
      .where(eq(projectRepos.githubRepoId, githubRepoId))
      .orderBy(asc(projectRepos.id));
    return rows.flatMap((row) => {
      const repo = toProjectRepo(row);
      return repo ? [repo] : [];
    });
  }

  async findTaskByGithubIssueId(
    projectId: string,
    githubIssueId: bigint,
  ): Promise<TaskRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(tasks)
      .where(and(eq(tasks.projectId, projectId), eq(tasks.githubIssueId, githubIssueId)))
      .limit(1);
    return row ? toTask(row) : undefined;
  }

  async findGithubInstallationByInstallationId(
    installationId: bigint,
  ): Promise<GithubInstallationRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(githubInstallations)
      .where(eq(githubInstallations.installationId, installationId))
      .limit(1);
    return row ? toGithubInstallation(row) : undefined;
  }

  async upsertGithubInstallation(row: GithubInstallationRecord): Promise<GithubInstallationRecord> {
    const [stored] = await this.db
      .insert(githubInstallations)
      .values({
        id: row.id,
        orgId: row.orgId,
        installationId: row.installationId,
        accountLogin: row.accountLogin,
        createdAt: row.createdAt,
      })
      .onConflictDoUpdate({
        target: githubInstallations.installationId,
        set: { orgId: row.orgId, accountLogin: row.accountLogin },
      })
      .returning();
    if (!stored) {
      throw new Error("upsert github installation returned no row");
    }
    return toGithubInstallation(stored);
  }

  async findGithubSyncState(repoId: string): Promise<GithubSyncStateRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(githubSyncState)
      .where(eq(githubSyncState.repoId, repoId))
      .limit(1);
    return row ? toGithubSyncState(row) : undefined;
  }

  async upsertGithubSyncState(row: GithubSyncStateRecord): Promise<GithubSyncStateRecord> {
    const [stored] = await this.db
      .insert(githubSyncState)
      .values({
        repoId: row.repoId,
        lastCursor: row.lastCursor,
        lastSyncedAt: row.lastSyncedAt,
      })
      .onConflictDoUpdate({
        target: githubSyncState.repoId,
        set: { lastCursor: row.lastCursor, lastSyncedAt: row.lastSyncedAt },
      })
      .returning();
    if (!stored) {
      throw new Error("upsert github sync state returned no row");
    }
    return toGithubSyncState(stored);
  }
  } as TBase & Ctor<GithubStore>;
}
