import { slugCandidate, slugFromLogin } from "../slug.js";
import {
  higherOrgRole,
  higherProjectRole,
  OrgSlugTakenError,
  ProjectSlugTakenError,
  type OrgInviteRecord,
  type OrgMemberRecord,
  type OrgRecord,
  type ProjectInviteRecord,
  type ProjectMemberRecord,
  type ProjectRecord,
} from "../orgs/types.js";

export type UserRecord = {
  id: string;
  githubId: bigint | null;
  login: string;
  email: string | null;
  name: string | null;
  avatarUrl: string | null;
  passwordHash: string | null;
  createdAt: Date;
  updatedAt: Date;
};

export type SessionRecord = {
  id: string;
  userId: string;
  tokenHash: Buffer;
  createdAt: Date;
  lastSeenAt: Date;
  expiresAt: Date;
  revokedAt: Date | null;
  userAgent: string | null;
  ip: string | null;
};

export type {
  OrgInviteRecord,
  OrgMemberRecord,
  OrgRecord,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
} from "../orgs/types.js";
export {
  InviteTargetRequiredError,
  OrgSlugTakenError,
  ProjectSlugTakenError,
} from "../orgs/types.js";

export class LoginTakenError extends Error {
  override readonly name = "LoginTakenError";

  constructor() {
    super("login is already taken");
  }
}

export class BootstrapConsumedError extends Error {
  override readonly name = "BootstrapConsumedError";

  constructor() {
    super("bootstrap has already been consumed");
  }
}

export class GithubIdTakenError extends Error {
  override readonly name = "GithubIdTakenError";

  constructor() {
    super("github account is already linked");
  }
}

export interface AuthStore {
  hasAnyUser(): Promise<boolean>;
  findUserById(id: string): Promise<UserRecord | undefined>;
  findUserByLogin(login: string): Promise<UserRecord | undefined>;
  findUserByEmail(email: string): Promise<UserRecord | undefined>;
  findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined>;
  createUser(user: UserRecord): Promise<UserRecord>;
  createFirstUser(user: UserRecord): Promise<UserRecord>;
  createSession(session: SessionRecord): Promise<SessionRecord>;
  findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined>;
  updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void>;
  revokeSession(id: string, revokedAt: Date): Promise<void>;
  revokeUserSessions(userId: string, revokedAt: Date): Promise<void>;
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
  acceptOrgInvite(id: string, userId: string, acceptedAt: Date): Promise<OrgInviteRecord | undefined>;
  createProject(project: ProjectRecord, creatorUserId: string): Promise<ProjectRecord>;
  listProjectsForOrg(orgId: string, userId: string): Promise<ProjectRecord[]>;
  findProjectById(id: string): Promise<ProjectRecord | undefined>;
  updateProject(
    id: string,
    patch: { name?: string; description?: string; slug?: string },
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
}

function cloneUser(user: UserRecord): UserRecord {
  return { ...user };
}

function cloneSession(session: SessionRecord): SessionRecord {
  return {
    ...session,
    tokenHash: Buffer.from(session.tokenHash),
    createdAt: new Date(session.createdAt),
    lastSeenAt: new Date(session.lastSeenAt),
    expiresAt: new Date(session.expiresAt),
    revokedAt: session.revokedAt ? new Date(session.revokedAt) : null,
  };
}

function cloneOrg(org: OrgRecord): OrgRecord {
  return { ...org, createdAt: new Date(org.createdAt) };
}

function cloneOrgMember(member: OrgMemberRecord): OrgMemberRecord {
  return { ...member };
}

function cloneOrgInvite(invite: OrgInviteRecord): OrgInviteRecord {
  return {
    ...invite,
    expiresAt: new Date(invite.expiresAt),
    acceptedAt: invite.acceptedAt ? new Date(invite.acceptedAt) : null,
  };
}

function cloneProject(project: ProjectRecord): ProjectRecord {
  return {
    ...project,
    settings: { ...project.settings },
    createdAt: new Date(project.createdAt),
    updatedAt: new Date(project.updatedAt),
    deletedAt: project.deletedAt ? new Date(project.deletedAt) : null,
  };
}

function cloneProjectMember(member: ProjectMemberRecord): ProjectMemberRecord {
  return { ...member, createdAt: new Date(member.createdAt) };
}

function cloneProjectInvite(invite: ProjectInviteRecord): ProjectInviteRecord {
  return {
    ...invite,
    expiresAt: new Date(invite.expiresAt),
    acceptedAt: invite.acceptedAt ? new Date(invite.acceptedAt) : null,
  };
}

function emailsEqual(left: string | null | undefined, right: string | null | undefined): boolean {
  if (!left || !right) {
    return false;
  }
  return left.toLowerCase() === right.toLowerCase();
}

export class MemoryAuthStore implements AuthStore {
  private readonly users = new Map<string, UserRecord>();
  private readonly sessions = new Map<string, SessionRecord>();
  private readonly orgs = new Map<string, OrgRecord>();
  private readonly orgMembers = new Map<string, OrgMemberRecord>();
  private readonly orgInvites = new Map<string, OrgInviteRecord>();
  private readonly projects = new Map<string, ProjectRecord>();
  private readonly projectMembers = new Map<string, ProjectMemberRecord>();
  private readonly projectInvites = new Map<string, ProjectInviteRecord>();
  private writeTail: Promise<void> = Promise.resolve();

  private orgMemberKey(orgId: string, userId: string): string {
    return `${orgId}:${userId}`;
  }

  private projectMemberKey(projectId: string, userId: string): string {
    return `${projectId}:${userId}`;
  }

  private enqueueWrite<T>(fn: () => T): Promise<T> {
    const run = this.writeTail.then(fn);
    this.writeTail = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  async findUserById(id: string): Promise<UserRecord | undefined> {
    const user = this.users.get(id);
    return user ? cloneUser(user) : undefined;
  }

  async findUserByLogin(login: string): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (user.login === login) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (user.githubId === githubId) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async findUserByEmail(email: string): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (emailsEqual(user.email, email)) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async hasAnyUser(): Promise<boolean> {
    return this.users.size > 0;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    return this.enqueueWrite(() => this.insertUser(user));
  }

  async createFirstUser(user: UserRecord): Promise<UserRecord> {
    return this.enqueueWrite(() => {
      if (this.users.size > 0) {
        throw new BootstrapConsumedError();
      }
      return this.insertUser(user);
    });
  }

  private insertUser(user: UserRecord): UserRecord {
    for (const existing of this.users.values()) {
      if (existing.login === user.login) {
        throw new LoginTakenError();
      }
      if (user.githubId !== null && existing.githubId === user.githubId) {
        throw new GithubIdTakenError();
      }
    }
    this.users.set(user.id, cloneUser(user));
    return cloneUser(user);
  }

  async createSession(session: SessionRecord): Promise<SessionRecord> {
    this.sessions.set(session.id, cloneSession(session));
    return cloneSession(session);
  }

  async findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined> {
    for (const session of this.sessions.values()) {
      if (session.tokenHash.equals(tokenHash)) {
        return cloneSession(session);
      }
    }
    return undefined;
  }

  async updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void> {
    const session = this.sessions.get(id);
    if (!session) {
      return;
    }
    session.lastSeenAt = new Date(lastSeenAt);
    session.expiresAt = new Date(expiresAt);
  }

  async revokeSession(id: string, revokedAt: Date): Promise<void> {
    const session = this.sessions.get(id);
    if (!session || session.revokedAt) {
      return;
    }
    session.revokedAt = new Date(revokedAt);
  }

  async revokeUserSessions(userId: string, revokedAt: Date): Promise<void> {
    for (const session of this.sessions.values()) {
      if (session.userId === userId && !session.revokedAt) {
        session.revokedAt = new Date(revokedAt);
      }
    }
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
    result.sort((a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id));
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
    const existing = this.orgMembers.get(this.orgMemberKey(member.orgId, member.userId));
    const next = {
      ...member,
      role: existing ? higherOrgRole(existing.role, member.role) : member.role,
    };
    this.orgMembers.set(this.orgMemberKey(member.orgId, member.userId), cloneOrgMember(next));
    return cloneOrgMember(next);
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
    result.sort((a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id));
    return result;
  }

  async findProjectById(id: string): Promise<ProjectRecord | undefined> {
    const project = this.projects.get(id);
    return project ? cloneProject(project) : undefined;
  }

  async updateProject(
    id: string,
    patch: { name?: string; description?: string; slug?: string },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined> {
    return this.enqueueWrite(() => {
      const project = this.projects.get(id);
      if (!project || project.deletedAt) {
        return undefined;
      }
      if (patch.slug && patch.slug !== project.slug) {
        for (const existing of this.projects.values()) {
          if (existing.id !== id && existing.orgId === project.orgId && existing.slug === patch.slug) {
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
    const existing = this.projectMembers.get(this.projectMemberKey(member.projectId, member.userId));
    const next = {
      ...member,
      role: existing ? higherProjectRole(existing.role, member.role) : member.role,
      createdAt: existing?.createdAt ?? member.createdAt,
    };
    this.projectMembers.set(this.projectMemberKey(member.projectId, member.userId), cloneProjectMember(next));
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
}
