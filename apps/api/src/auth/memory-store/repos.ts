import { UniqueViolationError } from "../errors.js";
import type { ProjectRepoRecord, CodeOwnerRecord } from "../../context/types.js";
import type { ProjectRepoRef } from "../../repos/store.js";
import { cloneProjectRepo, cloneCodeOwner, cloneSidecar } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryRepos<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryRepos extends Base {
  async listProjectRepos(projectId: string): Promise<ProjectRepoRecord[]> {
    const result: ProjectRepoRecord[] = [];
    for (const repo of this.projectRepos.values()) {
      if (repo.projectId === projectId) {
        result.push(cloneProjectRepo(repo));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async findProjectRepoById(id: string): Promise<ProjectRepoRecord | undefined> {
    const repo = this.projectRepos.get(id);
    return repo ? cloneProjectRepo(repo) : undefined;
  }

  async listCodeOwners(repoId: string): Promise<CodeOwnerRecord[]> {
    const result: CodeOwnerRecord[] = [];
    for (const row of this.codeOwners.values()) {
      if (row.repoId === repoId) {
        result.push(cloneCodeOwner(row));
      }
    }
    result.sort((a, b) => a.pathPattern.localeCompare(b.pathPattern) || a.id.localeCompare(b.id));
    return result;
  }

  async upsertCodeOwners(repoId: string, rows: CodeOwnerRecord[]): Promise<CodeOwnerRecord[]> {
    return this.enqueueWrite(() => this.upsertCodeOwnersUnlocked(repoId, rows));
  }

  override upsertCodeOwnersUnlocked(repoId: string, rows: CodeOwnerRecord[]): CodeOwnerRecord[] {
    for (const [id, existing] of this.codeOwners) {
      if (existing.repoId === repoId && existing.source === "codeowners") {
        this.codeOwners.delete(id);
      }
    }
    const written: CodeOwnerRecord[] = [];
    const byPattern = new Map<string, CodeOwnerRecord>();
    for (const row of rows) {
      byPattern.set(row.pathPattern, row);
    }
    for (const row of byPattern.values()) {
      const stored = cloneCodeOwner({ ...row, repoId });
      this.codeOwners.set(stored.id, stored);
      written.push(cloneCodeOwner(stored));
    }
    written.sort((a, b) => a.pathPattern.localeCompare(b.pathPattern) || a.id.localeCompare(b.id));
    return written;
  }

  async findProjectRepo(id: string): Promise<ProjectRepoRef | undefined> {
    const repo = await this.findProjectRepoById(id);
    return repo ? { id: repo.id, projectId: repo.projectId } : undefined;
  }

  async createProjectRepo(repo: ProjectRepoRecord): Promise<ProjectRepoRecord> {
    return this.enqueueWrite(() => {
      if (this.projectRepos.has(repo.id)) {
        throw new UniqueViolationError("project_repos_pkey");
      }
      if (repo.provider === "github" && repo.githubRepoId !== null) {
        for (const existing of this.projectRepos.values()) {
          if (
            existing.projectId === repo.projectId &&
            existing.githubRepoId !== null &&
            existing.githubRepoId === repo.githubRepoId
          ) {
            throw new UniqueViolationError("project_repos_project_id_github_repo_id_unique");
          }
        }
      }
      if (repo.provider === "local" && repo.localRootHint) {
        for (const existing of this.projectRepos.values()) {
          if (
            existing.projectId === repo.projectId &&
            existing.provider === "local" &&
            existing.localRootHint === repo.localRootHint
          ) {
            throw new UniqueViolationError("project_repos_local_root");
          }
        }
      }
      this.projectRepos.set(repo.id, cloneProjectRepo(repo));
      return cloneProjectRepo(repo);
    });
  }

  async updateProjectRepoIndex(
    id: string,
    patch: { lastIndexedSha?: string | null; lastIndexedAt?: Date | null },
  ): Promise<ProjectRepoRecord | undefined> {
    return this.enqueueWrite(() => {
      const repo = this.projectRepos.get(id);
      if (!repo) {
        return undefined;
      }
      if (patch.lastIndexedSha !== undefined) {
        repo.lastIndexedSha = patch.lastIndexedSha;
      }
      if (patch.lastIndexedAt !== undefined) {
        repo.lastIndexedAt = patch.lastIndexedAt ? new Date(patch.lastIndexedAt) : null;
      }
      return cloneProjectRepo(repo);
    });
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
    return this.enqueueWrite(() => {
      const existing = [...this.sidecarConnections.values()].find(
        (row) => row.repoId === input.repoId,
      );
      if (existing) {
        existing.tokenId = input.tokenId;
        existing.lastSeenAt = new Date(input.now);
        return cloneSidecar(existing);
      }
      const created = {
        id: input.id,
        repoId: input.repoId,
        tokenId: input.tokenId,
        connectedAt: new Date(input.now),
        lastSeenAt: new Date(input.now),
      };
      this.sidecarConnections.set(created.id, created);
      return cloneSidecar(created);
    });
  }

  async findSidecarConnectionByRepoId(
    repoId: string,
  ): Promise<
    { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date } | undefined
  > {
    for (const row of this.sidecarConnections.values()) {
      if (row.repoId === repoId) {
        return cloneSidecar(row);
      }
    }
    return undefined;
  }

  async updateProjectRepo(
    id: string,
    patch: { indexMode?: ProjectRepoRecord["indexMode"] },
  ): Promise<ProjectRepoRecord | undefined> {
    return this.enqueueWrite(() => {
      const repo = this.projectRepos.get(id);
      if (!repo) {
        return undefined;
      }
      if (patch.indexMode !== undefined) {
        repo.indexMode = patch.indexMode;
      }
      return cloneProjectRepo(repo);
    });
  }

  async consumeCloneInvalidation(
    repoId: string,
    now: Date,
  ): Promise<{ id: string; repoId: string; sha: string | null; createdAt: Date } | undefined> {
    return this.enqueueWrite(() => {
      const pending = [...this.cloneInvalidations.values()]
        .filter((row) => row.repoId === repoId && row.consumedAt === null)
        .sort((a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id));
      const next = pending[0];
      if (!next) {
        return undefined;
      }
      next.consumedAt = new Date(now);
      return {
        id: next.id,
        repoId: next.repoId,
        sha: next.sha,
        createdAt: new Date(next.createdAt),
      };
    });
  }

  seedProjectRepo(repo: ProjectRepoRecord): void {
    this.projectRepos.set(repo.id, cloneProjectRepo(repo));
  }

  seedCloneInvalidation(row: {
    id: string;
    repoId: string;
    sha?: string | null;
    createdAt: Date;
    consumedAt?: Date | null;
  }): void {
    this.cloneInvalidations.set(row.id, {
      id: row.id,
      repoId: row.repoId,
      sha: row.sha ?? null,
      createdAt: new Date(row.createdAt),
      consumedAt: row.consumedAt ? new Date(row.consumedAt) : null,
    });
  }
  };
}
