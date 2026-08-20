import type { CodeOwnerRecord, ProjectRepoRecord } from "../context/types.js";

export type ProjectRepoRef = {
  id: string;
  projectId: string;
};

export type SidecarConnectionRecord = {
  id: string;
  repoId: string;
  tokenId: string;
  connectedAt: Date;
  lastSeenAt: Date;
};

export interface RepoStore {
  listProjectRepos(projectId: string): Promise<ProjectRepoRecord[]>;
  findProjectRepoById(id: string): Promise<ProjectRepoRecord | undefined>;
  upsertCodeOwners(repoId: string, rows: CodeOwnerRecord[]): Promise<CodeOwnerRecord[]>;
  listCodeOwners(repoId: string): Promise<CodeOwnerRecord[]>;
  findProjectRepo(id: string): Promise<ProjectRepoRef | undefined>;
  createProjectRepo(repo: ProjectRepoRecord): Promise<ProjectRepoRecord>;
  updateProjectRepoIndex(
    id: string,
    patch: { lastIndexedSha?: string | null; lastIndexedAt?: Date | null },
  ): Promise<ProjectRepoRecord | undefined>;
  upsertSidecarConnection(input: {
    id: string;
    repoId: string;
    tokenId: string;
    now: Date;
  }): Promise<SidecarConnectionRecord>;
  findSidecarConnectionByRepoId(repoId: string): Promise<SidecarConnectionRecord | undefined>;
  updateProjectRepo(
    id: string,
    patch: { indexMode?: ProjectRepoRecord["indexMode"] },
  ): Promise<ProjectRepoRecord | undefined>;
  consumeCloneInvalidation(
    repoId: string,
    now: Date,
  ): Promise<{ id: string; repoId: string; sha: string | null; createdAt: Date } | undefined>;
}
