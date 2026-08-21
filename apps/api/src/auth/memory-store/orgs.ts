import { OrgSlugTakenError, ProjectSlugTakenError, higherOrgRole, higherProjectRole } from "../../orgs/types.js";
import { slugCandidate, slugFromLogin } from "../../slug.js";
import { uuidv7 } from "@beacon/shared";
import { DEFAULT_PROJECT_LABELS, DEFAULT_PROJECT_LABEL_STATUS, DEFAULT_SECURITY_CONSTRAINTS, DEFAULT_SECURITY_CONSTRAINT_KIND, DEFAULT_SECURITY_CONSTRAINT_STATUS } from "@beacon/context";
import type { UserRecord } from "../identity.js";
import type { OrgRecord, OrgMemberRecord, OrgInviteRecord, ProjectRecord, ProjectMemberRecord, ProjectInviteRecord } from "../../orgs/types.js";
import { cloneOrg, cloneOrgMember, cloneOrgInvite, cloneProject, cloneProjectMember, cloneProjectInvite } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryOrgs<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryOrgs extends Base {
  orgMemberKey(orgId: string, userId: string): string {
    return `${orgId}:${userId}`;
  }

  projectMemberKey(projectId: string, userId: string): string {
    return `${projectId}:${userId}`;
  }

  async ensurePersonalOrg(user: UserRecord, now: Date): Promise<OrgRecord> {
    return this.enqueueWrite(() => {
      const owned = this.orgs.get(user.id);
      if (owned?.kind === "personal") {
        this.orgMembers.set(this.orgMemberKey(owned.id, user.id), {
          orgId: owned.id,
          userId: user.id,
          role: "owner",
        });
        return cloneOrg(owned);
      }

      const base = slugFromLogin(user.login);
      let slug = base;
      for (let attempt = 1; attempt < 1000; attempt += 1) {
        slug = slugCandidate(base, attempt);
        if (![...this.orgs.values()].some((org) => org.slug === slug)) {
          break;
        }
      }

      const org: OrgRecord = {
        id: user.id,
        slug,
        name: user.name ?? user.login,
        kind: "personal",
        createdAt: now,
      };
      this.orgs.set(org.id, cloneOrg(org));
      this.orgMembers.set(this.orgMemberKey(org.id, user.id), {
        orgId: org.id,
        userId: user.id,
        role: "owner",
      });
      return cloneOrg(org);
    });
  }

  async listOrgsForUser(userId: string): Promise<OrgRecord[]> {
    const result: OrgRecord[] = [];
    for (const member of this.orgMembers.values()) {
      if (member.userId !== userId) {
        continue;
      }
      const org = this.orgs.get(member.orgId);
      if (org) {
        result.push(cloneOrg(org));
      }
    }
    result.sort(
      (a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findOrgById(id: string): Promise<OrgRecord | undefined> {
    const org = this.orgs.get(id);
    return org ? cloneOrg(org) : undefined;
  }

  async findOrgBySlug(slug: string): Promise<OrgRecord | undefined> {
    for (const org of this.orgs.values()) {
      if (org.slug === slug) {
        return cloneOrg(org);
      }
    }
    return undefined;
  }

  async createTeamOrg(org: OrgRecord, ownerUserId: string): Promise<OrgRecord> {
    return this.enqueueWrite(() => {
      if ([...this.orgs.values()].some((existing) => existing.slug === org.slug)) {
        throw new OrgSlugTakenError();
      }
      this.orgs.set(org.id, cloneOrg(org));
      this.orgMembers.set(this.orgMemberKey(org.id, ownerUserId), {
        orgId: org.id,
        userId: ownerUserId,
        role: "owner",
      });
      return cloneOrg(org);
    });
  }

  async listOrgMembers(orgId: string): Promise<OrgMemberRecord[]> {
    const result: OrgMemberRecord[] = [];
    for (const member of this.orgMembers.values()) {
      if (member.orgId === orgId) {
        result.push(cloneOrgMember(member));
      }
    }
    result.sort((a, b) => a.userId.localeCompare(b.userId));
    return result;
  }

  async findOrgMember(orgId: string, userId: string): Promise<OrgMemberRecord | undefined> {
    const member = this.orgMembers.get(this.orgMemberKey(orgId, userId));
    return member ? cloneOrgMember(member) : undefined;
  }

  async upsertOrgMember(member: OrgMemberRecord): Promise<OrgMemberRecord> {
    this.orgMembers.set(this.orgMemberKey(member.orgId, member.userId), cloneOrgMember(member));
    return cloneOrgMember(member);
  }

  async createOrgInvite(invite: OrgInviteRecord): Promise<OrgInviteRecord> {
    this.orgInvites.set(invite.id, cloneOrgInvite(invite));
    return cloneOrgInvite(invite);
  }

  async findOrgInviteById(id: string): Promise<OrgInviteRecord | undefined> {
    const invite = this.orgInvites.get(id);
    return invite ? cloneOrgInvite(invite) : undefined;
  }

  async acceptOrgInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<OrgInviteRecord | undefined> {
    return this.enqueueWrite(() => {
      const invite = this.orgInvites.get(id);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      invite.acceptedAt = new Date(acceptedAt);
      const existing = this.orgMembers.get(this.orgMemberKey(invite.orgId, userId));
      this.orgMembers.set(this.orgMemberKey(invite.orgId, userId), {
        orgId: invite.orgId,
        userId,
        role: existing ? higherOrgRole(existing.role, invite.role) : invite.role,
      });
      return cloneOrgInvite(invite);
    });
  }

  async createProject(project: ProjectRecord, creatorUserId: string): Promise<ProjectRecord> {
    return this.enqueueWrite(() => {
      for (const existing of this.projects.values()) {
        if (existing.orgId === project.orgId && existing.slug === project.slug) {
          throw new ProjectSlugTakenError();
        }
      }
      this.projects.set(project.id, cloneProject(project));
      this.projectMembers.set(this.projectMemberKey(project.id, creatorUserId), {
        projectId: project.id,
        userId: creatorUserId,
        role: "admin",
        createdAt: project.createdAt,
      });
      this.seedDefaultSecurityConstraints(project.id, project.createdAt);
      this.seedDefaultProjectLabels(project.id, project.createdAt);
      return cloneProject(project);
    });
  }

  async listProjectsForOrg(orgId: string, userId: string): Promise<ProjectRecord[]> {
    const result: ProjectRecord[] = [];
    for (const project of this.projects.values()) {
      if (project.orgId !== orgId || project.deletedAt) {
        continue;
      }
      if (!this.projectMembers.has(this.projectMemberKey(project.id, userId))) {
        continue;
      }
      result.push(cloneProject(project));
    }
    result.sort(
      (a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findProjectById(id: string): Promise<ProjectRecord | undefined> {
    const project = this.projects.get(id);
    return project ? cloneProject(project) : undefined;
  }

  async updateProject(
    id: string,
    patch: {
      name?: string;
      description?: string;
      slug?: string;
      settings?: Record<string, unknown>;
    },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined> {
    return this.enqueueWrite(() => {
      const project = this.projects.get(id);
      if (!project || project.deletedAt) {
        return undefined;
      }
      if (patch.slug && patch.slug !== project.slug) {
        for (const existing of this.projects.values()) {
          if (
            existing.id !== id &&
            existing.orgId === project.orgId &&
            existing.slug === patch.slug
          ) {
            throw new ProjectSlugTakenError();
          }
        }
        project.slug = patch.slug;
      }
      if (patch.name !== undefined) {
        project.name = patch.name;
      }
      if (patch.description !== undefined) {
        project.description = patch.description;
      }
      if (patch.settings !== undefined) {
        project.settings = { ...patch.settings };
      }
      project.updatedAt = new Date(updatedAt);
      return cloneProject(project);
    });
  }

  async softDeleteProject(id: string, deletedAt: Date): Promise<ProjectRecord | undefined> {
    const project = this.projects.get(id);
    if (!project || project.deletedAt) {
      return undefined;
    }
    project.deletedAt = new Date(deletedAt);
    project.updatedAt = new Date(deletedAt);
    return cloneProject(project);
  }

  async listProjectMembers(projectId: string): Promise<ProjectMemberRecord[]> {
    const result: ProjectMemberRecord[] = [];
    for (const member of this.projectMembers.values()) {
      if (member.projectId === projectId) {
        result.push(cloneProjectMember(member));
      }
    }
    result.sort((a, b) => a.userId.localeCompare(b.userId));
    return result;
  }

  async findProjectMember(
    projectId: string,
    userId: string,
  ): Promise<ProjectMemberRecord | undefined> {
    const member = this.projectMembers.get(this.projectMemberKey(projectId, userId));
    return member ? cloneProjectMember(member) : undefined;
  }

  async upsertProjectMember(member: ProjectMemberRecord): Promise<ProjectMemberRecord> {
    const existing = this.projectMembers.get(
      this.projectMemberKey(member.projectId, member.userId),
    );
    const next = {
      ...member,
      createdAt: existing?.createdAt ?? member.createdAt,
    };
    this.projectMembers.set(
      this.projectMemberKey(member.projectId, member.userId),
      cloneProjectMember(next),
    );
    return cloneProjectMember(next);
  }

  async createProjectInvite(invite: ProjectInviteRecord): Promise<ProjectInviteRecord> {
    this.projectInvites.set(invite.id, cloneProjectInvite(invite));
    return cloneProjectInvite(invite);
  }

  async findProjectInviteById(id: string): Promise<ProjectInviteRecord | undefined> {
    const invite = this.projectInvites.get(id);
    return invite ? cloneProjectInvite(invite) : undefined;
  }

  async acceptProjectInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<ProjectInviteRecord | undefined> {
    return this.enqueueWrite(() => {
      const invite = this.projectInvites.get(id);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      invite.acceptedAt = new Date(acceptedAt);
      const existing = this.projectMembers.get(this.projectMemberKey(invite.projectId, userId));
      this.projectMembers.set(this.projectMemberKey(invite.projectId, userId), {
        projectId: invite.projectId,
        userId,
        role: existing ? higherProjectRole(existing.role, invite.role) : invite.role,
        createdAt: existing?.createdAt ?? acceptedAt,
      });
      return cloneProjectInvite(invite);
    });
  }

  override seedDefaultSecurityConstraints(projectId: string, createdAt: Date): void {
    const existing = [...this.constraints.values()].filter(
      (constraint) =>
        constraint.projectId === projectId &&
        constraint.kind === DEFAULT_SECURITY_CONSTRAINT_KIND &&
        constraint.status === DEFAULT_SECURITY_CONSTRAINT_STATUS,
    );
    const existingBodies = new Set(existing.map((constraint) => constraint.body));
    let offset = 0;
    for (const body of DEFAULT_SECURITY_CONSTRAINTS) {
      if (existingBodies.has(body)) {
        continue;
      }
      const id = uuidv7(createdAt.getTime() + offset);
      offset += 1;
      this.constraints.set(id, {
        id,
        projectId,
        kind: DEFAULT_SECURITY_CONSTRAINT_KIND,
        body,
        scopePath: "",
        status: DEFAULT_SECURITY_CONSTRAINT_STATUS,
        createdAt: new Date(createdAt),
      });
    }
  }

  override seedDefaultProjectLabels(projectId: string, createdAt: Date): void {
    const existingSlugs = new Set(
      [...this.labels.values()]
        .filter((label) => label.projectId === projectId)
        .map((label) => label.slug),
    );
    let offset = 0;
    for (const seed of DEFAULT_PROJECT_LABELS) {
      if (existingSlugs.has(seed.slug)) {
        continue;
      }
      const id = uuidv7(createdAt.getTime() + offset);
      offset += 1;
      this.labels.set(id, {
        id,
        projectId,
        slug: seed.slug,
        name: seed.name,
        description: seed.description,
        color: seed.color,
        status: DEFAULT_PROJECT_LABEL_STATUS,
        createdAt: new Date(createdAt),
        paths: [],
      });
    }
  }

  async listDeletedProjects(): Promise<ProjectRecord[]> {
    const result: ProjectRecord[] = [];
    for (const project of this.projects.values()) {
      if (project.deletedAt) {
        result.push(cloneProject(project));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async setDefaultRepoIfEmpty(
    projectId: string,
    repoId: string,
    updatedAt: Date,
  ): Promise<ProjectRecord> {
    return this.enqueueWrite(() => {
      const project = this.projects.get(projectId);
      if (!project) {
        throw new Error("project not found");
      }
      if (!project.defaultRepoId) {
        project.defaultRepoId = repoId;
        project.updatedAt = new Date(updatedAt);
      }
      return cloneProject(project);
    });
  }

  seedProject(project: ProjectRecord, creatorUserId: string): void {
    this.projects.set(project.id, cloneProject(project));
    this.projectMembers.set(this.projectMemberKey(project.id, creatorUserId), {
      projectId: project.id,
      userId: creatorUserId,
      role: "admin",
      createdAt: project.createdAt,
    });
  }
  };
}
