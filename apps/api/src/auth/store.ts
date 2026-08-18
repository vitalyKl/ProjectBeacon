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
import { wouldCreateCycle } from "../roadmap/cycle.js";
import {
  DependencyCycleError,
  IDEMPOTENCY_TTL_MS,
  VersionConflictError,
  type ActivityEventRecord,
  type IdempotencyActorType,
  type MilestoneRecord,
  type TaskCommentRecord,
  type TaskDependencyRecord,
  type TaskPatch,
  type TaskRecord,
} from "../roadmap/types.js";
import type {
  AgentSessionRef,
  ApprovalRecord,
  RateBucketRecord,
  TokenRecord,
} from "../tokens/types.js";

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
export {
  DependencyCycleError,
  VersionConflictError,
} from "../roadmap/types.js";
export type {
  ActivityEventRecord,
  MilestoneRecord,
  TaskCommentRecord,
  TaskDependencyRecord,
  TaskPatch,
  TaskRecord,
} from "../roadmap/types.js";
export type {
  AgentSessionRef,
  ApprovalRecord,
  RateBucketRecord,
  TokenRecord,
} from "../tokens/types.js";

export type IdempotentWrites = {
  createTask(task: TaskRecord): Promise<TaskRecord>;
  createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord>;
  writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord>;
};

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
  createApiToken(token: TokenRecord): Promise<TokenRecord>;
  listApiTokens(projectId: string): Promise<TokenRecord[]>;
  findApiTokenById(id: string): Promise<TokenRecord | undefined>;
  findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined>;
  touchApiToken(id: string, lastUsedAt: Date): Promise<void>;
  revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined>;
  createApproval(approval: ApprovalRecord): Promise<ApprovalRecord>;
  listApprovals(projectId: string, status?: ApprovalRecord["status"]): Promise<ApprovalRecord[]>;
  findApprovalById(id: string): Promise<ApprovalRecord | undefined>;
  resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined>;
  consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord>;
  createMilestone(milestone: MilestoneRecord): Promise<MilestoneRecord>;
  listMilestones(projectId: string): Promise<MilestoneRecord[]>;
  findMilestoneById(id: string): Promise<MilestoneRecord | undefined>;
  createTask(task: TaskRecord): Promise<TaskRecord>;
  listTasks(projectId: string): Promise<TaskRecord[]>;
  findTaskById(id: string): Promise<TaskRecord | undefined>;
  updateTask(
    id: string,
    expectedVersion: number,
    patch: TaskPatch,
    updatedAt: Date,
    options?: { releaseLock?: boolean },
  ): Promise<{ task: TaskRecord; lockReleased: boolean } | undefined>;
  softDeleteTask(id: string, deletedAt: Date): Promise<TaskRecord | undefined>;
  createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord>;
  addDependency(dependency: TaskDependencyRecord): Promise<TaskDependencyRecord>;
  writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord>;
  listActivity(
    projectId: string,
    filters?: { objectType?: string; objectId?: string },
  ): Promise<ActivityEventRecord[]>;
  withIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: (writes: IdempotentWrites) => Promise<unknown>,
  ): Promise<unknown>;
  findAgentSessionById(id: string): Promise<AgentSessionRef | undefined>;
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

function cloneToken(token: TokenRecord): TokenRecord {
  return {
    ...token,
    tokenHash: Buffer.from(token.tokenHash),
    scopes: [...token.scopes],
    lastUsedAt: token.lastUsedAt ? new Date(token.lastUsedAt) : null,
    expiresAt: token.expiresAt ? new Date(token.expiresAt) : null,
    revokedAt: token.revokedAt ? new Date(token.revokedAt) : null,
    createdAt: new Date(token.createdAt),
  };
}

function cloneApproval(approval: ApprovalRecord): ApprovalRecord {
  return {
    ...approval,
    payload: { ...approval.payload },
    requestedAt: new Date(approval.requestedAt),
    resolvedAt: approval.resolvedAt ? new Date(approval.resolvedAt) : null,
  };
}

function cloneRateBucket(bucket: RateBucketRecord): RateBucketRecord {
  return {
    ...bucket,
    windowStart: new Date(bucket.windowStart),
    bytes: bucket.bytes,
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
  private readonly apiTokens = new Map<string, TokenRecord>();
  private readonly approvals = new Map<string, ApprovalRecord>();
  private readonly rateBuckets = new Map<string, RateBucketRecord>();
  private readonly milestones = new Map<string, MilestoneRecord>();
  private readonly tasks = new Map<string, TaskRecord>();
  private readonly comments = new Map<string, TaskCommentRecord>();
  private readonly dependencies: TaskDependencyRecord[] = [];
  private readonly activity = new Map<string, ActivityEventRecord>();
  private readonly idempotency = new Map<
    string,
    { response: unknown; createdAt: Date }
  >();
  private readonly agentSessions = new Map<string, AgentSessionRef>();
  private writeTail: Promise<void> = Promise.resolve();

  private orgMemberKey(orgId: string, userId: string): string {
    return `${orgId}:${userId}`;
  }

  private projectMemberKey(projectId: string, userId: string): string {
    return `${projectId}:${userId}`;
  }

  private enqueueWrite<T>(fn: () => T | Promise<T>): Promise<T> {
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

  async createMilestone(milestone: MilestoneRecord): Promise<MilestoneRecord> {
    this.milestones.set(milestone.id, cloneMilestone(milestone));
    return cloneMilestone(milestone);
  }

  async listMilestones(projectId: string): Promise<MilestoneRecord[]> {
    const result: MilestoneRecord[] = [];
    for (const milestone of this.milestones.values()) {
      if (milestone.projectId === projectId) {
        result.push(cloneMilestone(milestone));
      }
    }
    result.sort(
      (a, b) => a.sortOrder - b.sortOrder || a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findMilestoneById(id: string): Promise<MilestoneRecord | undefined> {
    const milestone = this.milestones.get(id);
    return milestone ? cloneMilestone(milestone) : undefined;
  }

  async createTask(task: TaskRecord): Promise<TaskRecord> {
    return this.enqueueWrite(() => this.insertTaskUnlocked(task));
  }

  async listTasks(projectId: string): Promise<TaskRecord[]> {
    const result: TaskRecord[] = [];
    for (const task of this.tasks.values()) {
      if (task.projectId === projectId && !task.deletedAt) {
        result.push(cloneTask(task));
      }
    }
    return result;
  }

  async findTaskById(id: string): Promise<TaskRecord | undefined> {
    const task = this.tasks.get(id);
    return task ? cloneTask(task) : undefined;
  }

  async updateTask(
    id: string,
    expectedVersion: number,
    patch: TaskPatch,
    updatedAt: Date,
    options?: { releaseLock?: boolean },
  ): Promise<{ task: TaskRecord; lockReleased: boolean } | undefined> {
    return this.enqueueWrite(() => {
      const task = this.tasks.get(id);
      if (!task || task.deletedAt) {
        return undefined;
      }
      if (task.version !== expectedVersion) {
        throw new VersionConflictError(cloneTask(task));
      }
      if (patch.title !== undefined) {
        task.title = patch.title;
      }
      if (patch.description !== undefined) {
        task.description = patch.description;
      }
      if (patch.status !== undefined) {
        task.status = patch.status;
      }
      if (patch.type !== undefined) {
        task.type = patch.type;
      }
      if (patch.priority !== undefined) {
        task.priority = patch.priority;
      }
      if (patch.milestoneId !== undefined) {
        task.milestoneId = patch.milestoneId;
      }
      if (patch.parentId !== undefined) {
        task.parentId = patch.parentId;
      }
      if (patch.assigneeUserId !== undefined) {
        task.assigneeUserId = patch.assigneeUserId;
      }
      if (patch.assigneeAgentName !== undefined) {
        task.assigneeAgentName = patch.assigneeAgentName;
      }
      if (patch.agentBrief !== undefined) {
        task.agentBrief = patch.agentBrief;
      }
      if (patch.linkedPaths !== undefined) {
        task.linkedPaths = patch.linkedPaths.map((path) => ({ ...path }));
      }
      const lockReleased = Boolean(options?.releaseLock && task.lockedBySessionId);
      if (options?.releaseLock) {
        task.lockedBySessionId = null;
        task.lockExpiresAt = null;
      }
      task.version += 1;
      task.updatedAt = new Date(updatedAt);
      return { task: cloneTask(task), lockReleased };
    });
  }

  async softDeleteTask(id: string, deletedAt: Date): Promise<TaskRecord | undefined> {
    const task = this.tasks.get(id);
    if (!task || task.deletedAt) {
      return undefined;
    }
    task.deletedAt = new Date(deletedAt);
    task.updatedAt = new Date(deletedAt);
    task.version += 1;
    return cloneTask(task);
  }

  async createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord> {
    return this.enqueueWrite(() => this.insertCommentUnlocked(comment));
  }

  async addDependency(dependency: TaskDependencyRecord): Promise<TaskDependencyRecord> {
    return this.enqueueWrite(() => {
      if (dependency.fromTaskId === dependency.toTaskId) {
        throw new DependencyCycleError();
      }
      const existing = this.dependencies.find(
        (row) =>
          row.fromTaskId === dependency.fromTaskId &&
          row.toTaskId === dependency.toTaskId &&
          row.type === dependency.type,
      );
      if (existing) {
        return { ...existing };
      }
      if (dependency.type === "blocks") {
        const from = this.tasks.get(dependency.fromTaskId);
        const blockEdges = this.dependencies.filter((row) => {
          if (row.type !== "blocks") {
            return false;
          }
          if (!from) {
            return true;
          }
          const edgeFrom = this.tasks.get(row.fromTaskId);
          return !edgeFrom || edgeFrom.projectId === from.projectId;
        });
        if (wouldCreateCycle(blockEdges, dependency.fromTaskId, dependency.toTaskId)) {
          throw new DependencyCycleError();
        }
      }
      this.dependencies.push({ ...dependency });
      return { ...dependency };
    });
  }

  async writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord> {
    return this.enqueueWrite(() => this.insertActivityUnlocked(event));
  }

  async listActivity(
    projectId: string,
    filters?: { objectType?: string; objectId?: string },
  ): Promise<ActivityEventRecord[]> {
    const result: ActivityEventRecord[] = [];
    for (const event of this.activity.values()) {
      if (event.projectId !== projectId) {
        continue;
      }
      if (filters?.objectType && event.objectType !== filters.objectType) {
        continue;
      }
      if (filters?.objectId && event.objectId !== filters.objectId) {
        continue;
      }
      result.push(cloneActivity(event));
    }
    return result;
  }

  async withIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: (writes: IdempotentWrites) => Promise<unknown>,
  ): Promise<unknown> {
    return this.enqueueWrite(async () => {
      const slot = idempotencyKey(actorType, actorId, key);
      const stored = this.idempotency.get(slot);
      if (stored && now.getTime() - stored.createdAt.getTime() <= IDEMPOTENCY_TTL_MS) {
        return structuredClone(stored.response);
      }
      this.idempotency.delete(slot);
      const writes: IdempotentWrites = {
        createTask: async (task) => this.insertTaskUnlocked(task),
        createComment: async (comment) => this.insertCommentUnlocked(comment),
        writeActivity: async (event) => this.insertActivityUnlocked(event),
      };
      const response = await produce(writes);
      this.idempotency.set(slot, {
        response: structuredClone(response),
        createdAt: new Date(now),
      });
      return structuredClone(response);
    });
  }

  private insertTaskUnlocked(task: TaskRecord): TaskRecord {
    this.tasks.set(task.id, cloneTask(task));
    return cloneTask(task);
  }

  private insertCommentUnlocked(comment: TaskCommentRecord): TaskCommentRecord {
    this.comments.set(comment.id, cloneComment(comment));
    return cloneComment(comment);
  }

  private insertActivityUnlocked(event: ActivityEventRecord): ActivityEventRecord {
    this.activity.set(event.id, cloneActivity(event));
    return cloneActivity(event);
  }
}

function cloneMilestone(milestone: MilestoneRecord): MilestoneRecord {
  return { ...milestone, createdAt: new Date(milestone.createdAt) };
}

function cloneTask(task: TaskRecord): TaskRecord {
  return {
    ...task,
    linkedPaths: task.linkedPaths.map((path) => ({ ...path })),
    lockExpiresAt: task.lockExpiresAt ? new Date(task.lockExpiresAt) : null,
    deletedAt: task.deletedAt ? new Date(task.deletedAt) : null,
    createdAt: new Date(task.createdAt),
    updatedAt: new Date(task.updatedAt),
  };
  async createApiToken(token: TokenRecord): Promise<TokenRecord> {
    this.apiTokens.set(token.id, cloneToken(token));
    return cloneToken(token);
  }

  async listApiTokens(projectId: string): Promise<TokenRecord[]> {
    const result: TokenRecord[] = [];
    for (const token of this.apiTokens.values()) {
      if (token.projectId === projectId) {
        result.push(cloneToken(token));
      }
    }
    result.sort((a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id));
    return result;
  }

  async findApiTokenById(id: string): Promise<TokenRecord | undefined> {
    const token = this.apiTokens.get(id);
    return token ? cloneToken(token) : undefined;
  }

  async findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined> {
    for (const token of this.apiTokens.values()) {
      if (token.tokenHash.equals(tokenHash)) {
        return cloneToken(token);
      }
    }
    return undefined;
  }

  async touchApiToken(id: string, lastUsedAt: Date): Promise<void> {
    const token = this.apiTokens.get(id);
    if (!token) {
      return;
    }
    token.lastUsedAt = new Date(lastUsedAt);
  }

  async revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined> {
    const token = this.apiTokens.get(id);
    if (!token || token.revokedAt) {
      return token ? cloneToken(token) : undefined;
    }
    token.revokedAt = new Date(revokedAt);
    return cloneToken(token);
  }

  async createApproval(approval: ApprovalRecord): Promise<ApprovalRecord> {
    this.approvals.set(approval.id, cloneApproval(approval));
    return cloneApproval(approval);
  }

  async listApprovals(
    projectId: string,
    status?: ApprovalRecord["status"],
  ): Promise<ApprovalRecord[]> {
    const result: ApprovalRecord[] = [];
    for (const approval of this.approvals.values()) {
      if (approval.projectId !== projectId) {
        continue;
      }
      if (status && approval.status !== status) {
        continue;
      }
      result.push(cloneApproval(approval));
    }
    result.sort(
      (a, b) => a.requestedAt.getTime() - b.requestedAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findApprovalById(id: string): Promise<ApprovalRecord | undefined> {
    const approval = this.approvals.get(id);
    return approval ? cloneApproval(approval) : undefined;
  }

  async resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined> {
    const approval = this.approvals.get(id);
    if (!approval || approval.status !== "pending") {
      return undefined;
    }
    approval.status = decision;
    approval.resolvedAt = new Date(resolvedAt);
    approval.resolvedBy = resolvedBy;
    return cloneApproval(approval);
  }

  async consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord> {
    const existing = this.rateBuckets.get(input.bucketKey);
    if (!existing || existing.windowStart.getTime() !== input.windowStart.getTime()) {
      const created: RateBucketRecord = {
        bucketKey: input.bucketKey,
        windowStart: new Date(input.windowStart),
        count: input.countDelta,
        bytes: BigInt(input.bytesDelta),
      };
      this.rateBuckets.set(input.bucketKey, created);
      return cloneRateBucket(created);
    }
    existing.count += input.countDelta;
    existing.bytes += BigInt(input.bytesDelta);
    return cloneRateBucket(existing);
  }

  /** Test helper until agent-session create exists. */
  putAgentSession(session: AgentSessionRef): void {
    this.agentSessions.set(session.id, { ...session });
  }

  async findAgentSessionById(id: string): Promise<AgentSessionRef | undefined> {
    const session = this.agentSessions.get(id);
    return session ? { ...session } : undefined;
  }
}

function cloneComment(comment: TaskCommentRecord): TaskCommentRecord {
  return { ...comment, createdAt: new Date(comment.createdAt) };
}

function cloneActivity(event: ActivityEventRecord): ActivityEventRecord {
  return {
    ...event,
    payload: { ...event.payload },
    createdAt: new Date(event.createdAt),
  };
}

function idempotencyKey(actorType: IdempotencyActorType, actorId: string, key: string): string {
  return `${actorType}:${actorId}:${key}`;
}
