import type { UserRecord } from "../auth/identity.js";
import type {
  OrgInviteRecord,
  OrgMemberRecord,
  OrgRecord,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
} from "./types.js";

export interface OrgStore {
  ensurePersonalOrg(user: UserRecord, now: Date): Promise<OrgRecord>;
  listOrgsForUser(userId: string): Promise<OrgRecord[]>;
  findOrgById(id: string): Promise<OrgRecord | undefined>;
  findOrgBySlug(slug: string): Promise<OrgRecord | undefined>;
  createTeamOrg(org: OrgRecord, ownerUserId: string): Promise<OrgRecord>;
  listOrgMembers(orgId: string): Promise<OrgMemberRecord[]>;
  findOrgMember(orgId: string, userId: string): Promise<OrgMemberRecord | undefined>;
  upsertOrgMember(member: OrgMemberRecord): Promise<OrgMemberRecord>;
  createOrgInvite(invite: OrgInviteRecord): Promise<OrgInviteRecord>;
  findOrgInviteById(id: string): Promise<OrgInviteRecord | undefined>;
  acceptOrgInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<OrgInviteRecord | undefined>;
  createProject(project: ProjectRecord, creatorUserId: string): Promise<ProjectRecord>;
  listProjectsForOrg(orgId: string, userId: string): Promise<ProjectRecord[]>;
  findProjectById(id: string): Promise<ProjectRecord | undefined>;
  updateProject(
    id: string,
    patch: {
      name?: string;
      description?: string;
      slug?: string;
      settings?: Record<string, unknown>;
    },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined>;
  softDeleteProject(id: string, deletedAt: Date): Promise<ProjectRecord | undefined>;
  listProjectMembers(projectId: string): Promise<ProjectMemberRecord[]>;
  findProjectMember(projectId: string, userId: string): Promise<ProjectMemberRecord | undefined>;
  upsertProjectMember(member: ProjectMemberRecord): Promise<ProjectMemberRecord>;
  createProjectInvite(invite: ProjectInviteRecord): Promise<ProjectInviteRecord>;
  findProjectInviteById(id: string): Promise<ProjectInviteRecord | undefined>;
  acceptProjectInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<ProjectInviteRecord | undefined>;
  listDeletedProjects(): Promise<ProjectRecord[]>;
  setDefaultRepoIfEmpty(projectId: string, repoId: string, updatedAt: Date): Promise<ProjectRecord>;
}
