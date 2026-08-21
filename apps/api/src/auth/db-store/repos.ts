import { codeOwners, projectRepos, sidecarConnections, githubCloneInvalidations, type Db } from "@beacon/db";
import { and, asc, desc, eq, isNull } from "drizzle-orm";
import { UniqueViolationError } from "../errors.js";
import type { ProjectRepoRecord, CodeOwnerRecord } from "../../context/types.js";
import type { ProjectRepoRef } from "../../repos/store.js";
import { uniqueConstraint, toProjectRepo, toCodeOwner, upsertCodeOwnersInTx, toSidecar } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { RepoStore } from "../../repos/store.js";

export function withDbRepos<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<RepoStore> {
  return class DbRepos extends Base {
  async listProjectRepos(projectId: string): Promise<ProjectRepoRecord[]> {
    const rows = await this.db
      .select()
      .from(projectRepos)
      .where(eq(projectRepos.projectId, projectId))
      .orderBy(asc(projectRepos.id));
    return rows.flatMap((row) => {
      const repo = toProjectRepo(row);
      return repo ? [repo] : [];
    });
  }

  async findProjectRepoById(id: string): Promise<ProjectRepoRecord | undefined> {
    const [row] = await this.db.select().from(projectRepos).where(eq(projectRepos.id, id)).limit(1);
    return row ? toProjectRepo(row) : undefined;
  }

  async listCodeOwners(repoId: string): Promise<CodeOwnerRecord[]> {
    const rows = await this.db
      .select()
      .from(codeOwners)
      .where(eq(codeOwners.repoId, repoId))
      .orderBy(asc(codeOwners.pathPattern), asc(codeOwners.id));
    return rows.map(toCodeOwner);
  }

  async upsertCodeOwners(repoId: string, rows: CodeOwnerRecord[]): Promise<CodeOwnerRecord[]> {
    const bound = this.writeTx.getStore();
    if (bound) {
      return upsertCodeOwnersInTx(bound, repoId, rows);
    }
    return this.db.transaction((tx) => upsertCodeOwnersInTx(tx as unknown as Db, repoId, rows));
  }

  async findProjectRepo(id: string): Promise<ProjectRepoRef | undefined> {
    const repo = await this.findProjectRepoById(id);
    return repo ? { id: repo.id, projectId: repo.projectId } : undefined;
  }

  async createProjectRepo(repo: ProjectRepoRecord): Promise<ProjectRepoRecord> {
    try {
      const [row] = await this.db
        .insert(projectRepos)
        .values({
          id: repo.id,
          projectId: repo.projectId,
          provider: repo.provider,
          remoteUrl: repo.remoteUrl,
          defaultBranch: repo.defaultBranch,
          githubRepoId: repo.githubRepoId,
          installationId: repo.installationId,
          localRootHint: repo.localRootHint,
          indexMode: repo.indexMode,
          lastIndexedSha: repo.lastIndexedSha,
          lastIndexedAt: repo.lastIndexedAt,
        })
        .returning();
      if (!row) {
        throw new Error("insert project repo returned no row");
      }
      const stored = toProjectRepo(row);
      if (!stored) {
        throw new Error("insert project repo returned invalid row");
      }
      return stored;
    } catch (error) {
      if (uniqueConstraint(error) === "project_repo") {
        throw new UniqueViolationError("project_repos");
      }
      throw error;
    }
  }

  async updateProjectRepoIndex(
    id: string,
    patch: { lastIndexedSha?: string | null; lastIndexedAt?: Date | null },
  ): Promise<ProjectRepoRecord | undefined> {
    const current = await this.findProjectRepoById(id);
    if (!current) {
      return undefined;
    }
    const [row] = await this.db
      .update(projectRepos)
      .set({
        lastIndexedSha:
          patch.lastIndexedSha === undefined ? current.lastIndexedSha : patch.lastIndexedSha,
        lastIndexedAt:
          patch.lastIndexedAt === undefined ? current.lastIndexedAt : patch.lastIndexedAt,
      })
      .where(eq(projectRepos.id, id))
      .returning();
    return row ? toProjectRepo(row) : undefined;
  }

  async upsertSidecarConnection(input: {
    id: string;
    repoId: string;
    tokenId: string;
    now: Date;
  }): Promise<{
    id: string;
    repoId: string;
    tokenId: string;
    connectedAt: Date;
    lastSeenAt: Date;
  }> {
    const [row] = await this.db
      .insert(sidecarConnections)
      .values({
        id: input.id,
        repoId: input.repoId,
        tokenId: input.tokenId,
        connectedAt: input.now,
        lastSeenAt: input.now,
      })
      .onConflictDoUpdate({
        target: sidecarConnections.repoId,
        set: { tokenId: input.tokenId, lastSeenAt: input.now },
      })
      .returning();
    if (!row) {
      throw new Error("upsert sidecar connection returned no row");
    }
    return toSidecar(row);
  }

  async findSidecarConnectionByRepoId(
    repoId: string,
  ): Promise<
    { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date } | undefined
  > {
    const [row] = await this.db
      .select()
      .from(sidecarConnections)
      .where(eq(sidecarConnections.repoId, repoId))
      .orderBy(desc(sidecarConnections.lastSeenAt))
      .limit(1);
    return row ? toSidecar(row) : undefined;
  }

  async updateProjectRepo(
    id: string,
    patch: { indexMode?: ProjectRepoRecord["indexMode"] },
  ): Promise<ProjectRepoRecord | undefined> {
    const current = await this.findProjectRepoById(id);
    if (!current) {
      return undefined;
    }
    const [row] = await this.db
      .update(projectRepos)
      .set({
        indexMode: patch.indexMode === undefined ? current.indexMode : patch.indexMode,
      })
      .where(eq(projectRepos.id, id))
      .returning();
    return row ? toProjectRepo(row) : undefined;
  }

  async consumeCloneInvalidation(
    repoId: string,
    now: Date,
  ): Promise<{ id: string; repoId: string; sha: string | null; createdAt: Date } | undefined> {
    return this.db.transaction(async (tx) => {
      const [row] = await tx
        .select()
        .from(githubCloneInvalidations)
        .where(
          and(
            eq(githubCloneInvalidations.repoId, repoId),
            isNull(githubCloneInvalidations.consumedAt),
          ),
        )
        .orderBy(asc(githubCloneInvalidations.createdAt), asc(githubCloneInvalidations.id))
        .limit(1);
      if (!row) {
        return undefined;
      }
      await tx
        .update(githubCloneInvalidations)
        .set({ consumedAt: now })
        .where(eq(githubCloneInvalidations.id, row.id));
      return {
        id: row.id,
        repoId: row.repoId,
        sha: row.sha,
        createdAt: row.createdAt,
      };
    });
  }
  } as TBase & Ctor<RepoStore>;
}
