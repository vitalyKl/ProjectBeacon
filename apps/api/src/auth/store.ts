import {
  DEFAULT_SECURITY_CONSTRAINTS,
  DEFAULT_SECURITY_CONSTRAINT_KIND,
  DEFAULT_SECURITY_CONSTRAINT_STATUS,
} from "@beacon/context";
import { uuidv7 } from "@beacon/shared";
import type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionRecord, ProjectRepoRecord } from "../context/types.js";
import { slugCandidate, slugFromLogin } from "../slug.js";
import { higherOrgRole, higherProjectRole, OrgSlugTakenError, ProjectSlugTakenError, type OrgInviteRecord, type OrgMemberRecord, type OrgRecord, type ProjectInviteRecord, type ProjectMemberRecord, type ProjectRecord } from "../orgs/types.js";
import { wouldCreateCycle } from "../roadmap/cycle.js";
import { DependencyCycleError, IDEMPOTENCY_TTL_MS, VersionConflictError, type ActivityEventRecord, type IdempotencyActorType, type MilestoneRecord, type TaskCommentRecord, type TaskDependencyRecord, type TaskPatch, type TaskRecord } from "../roadmap/types.js";
import type { AgentSessionRef, ApprovalRecord, RateBucketRecord, TokenRecord } from "../tokens/types.js";
export type { OrgInviteRecord, OrgMemberRecord, OrgRecord, ProjectInviteRecord, ProjectMemberRecord, ProjectRecord } from "../orgs/types.js";
export { InviteTargetRequiredError, OrgSlugTakenError, ProjectSlugTakenError } from "../orgs/types.js";
export { DependencyCycleError, VersionConflictError } from "../roadmap/types.js";
export type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionRecord, ProjectRepoRecord, DecisionPathLink } from "../context/types.js";
export type { ActivityEventRecord, MilestoneRecord, TaskCommentRecord, TaskDependencyRecord, TaskPatch, TaskRecord } from "../roadmap/types.js";
export type { AgentSessionRef, ApprovalRecord, RateBucketRecord, TokenRecord } from "../tokens/types.js";
import {
  InvalidReferenceError,
  isLockActive,
  LOCK_TTL_MS,
  SessionNotActiveError,
  TaskLockedError,
  type AgentSessionRecord,
  type FinishWorkInput,
  type FinishWorkResult,
  type HandoffRecord,
  type StartWorkInput,
  type StartWorkWriteResult,
} from "../sessions/types.js";

export type {
  AgentSessionRecord,
  FinishWorkResult,
  HandoffRecord,
} from "../sessions/types.js";
export {
  InvalidReferenceError,
  SessionNotActiveError,
  TaskLockedError,
} from "../sessions/types.js";

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

export type IdempotentWrites = {

  createTask(task: TaskRecord): Promise<TaskRecord>;
  createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord>;
  writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord>;
  startWork(input: StartWorkInput): Promise<StartWorkWriteResult>;
  createDecision(decision: DecisionRecord): Promise<DecisionRecord>;
  createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
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

export class UniqueViolationError extends Error {

  override readonly name = "UniqueViolationError";

  constructor(constraint: string) {
    super(`unique constraint violated: ${constraint}`);
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
  listContextNodes(projectId: string): Promise<ContextNodeRecord[]>;
  upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord>;
  listActiveConstraints(projectId: string): Promise<ConstraintRecord[]>;
  listConstraints(projectId: string): Promise<ConstraintRecord[]>;
  insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
  listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]>;
  insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord>;
  listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]>;
  listProjectRepos(projectId: string): Promise<ProjectRepoRecord[]>;
  findProjectRepoById(id: string): Promise<ProjectRepoRecord | undefined>;
  upsertCodeOwners(repoId: string, rows: CodeOwnerRecord[]): Promise<CodeOwnerRecord[]>;
  listCodeOwners(repoId: string): Promise<CodeOwnerRecord[]>;
  withIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: (writes: IdempotentWrites) => Promise<unknown>,
  ): Promise<unknown>;
  findAgentSessionById(id: string): Promise<AgentSessionRef | undefined>;
  listAgentSessions(projectId: string): Promise<AgentSessionRecord[]>;
  heartbeatSession(id: string, now: Date): Promise<AgentSessionRecord | undefined>;
  finishWork(input: FinishWorkInput): Promise<FinishWorkResult | undefined>;
  findLatestHandoffByTaskId(taskId: string): Promise<HandoffRecord | undefined>;
  findConstraintById(id: string): Promise<ConstraintRecord | undefined>;
  createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
  applyConstraint(id: string, appliedAt: Date): Promise<ConstraintRecord | undefined>;
  listDecisions(projectId: string): Promise<DecisionRecord[]>;
  findDecisionById(id: string): Promise<DecisionRecord | undefined>;
  createDecision(decision: DecisionRecord): Promise<DecisionRecord>;
  findProjectRepo(id: string): Promise<ProjectRepoRef | undefined>;
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
  private readonly contextNodes = new Map<string, ContextNodeRecord>();
  private readonly constraints = new Map<string, ConstraintRecord>();
  private readonly decisions = new Map<string, DecisionRecord>();
  private readonly contextRevisions = new Map<string, ContextRevisionRecord>();
  private readonly projectRepos = new Map<string, ProjectRepoRef>();
  private readonly codeOwners = new Map<string, CodeOwnerRecord>();
  private readonly idempotency = new Map<string, { response: unknown; createdAt: Date }>();
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
      (a, b) =>
        a.sortOrder - b.sortOrder ||
        a.createdAt.getTime() - b.createdAt.getTime() ||
        a.id.localeCompare(b.id),
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

  async listContextNodes(projectId: string): Promise<ContextNodeRecord[]> {
    const result: ContextNodeRecord[] = [];
    for (const node of this.contextNodes.values()) {
      if (node.projectId === projectId) {
        result.push(cloneContextNode(node));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  seedContextNode(node: ContextNodeRecord): void {
    this.contextNodes.set(node.id, cloneContextNode(node));
  }

  seedProjectRepo(repo: ProjectRepoRef): void {
    this.projectRepos.set(repo.id, { ...repo });
  }

  async upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return this.enqueueWrite(() => {
      const existing = this.findContextNodeByScope(node);
      if (existing) {
        existing.sections = node.sections.map((section) => ({ ...section }));
        existing.sectionsText = node.sectionsText;
        existing.source = node.source;
        existing.sourcePath = node.sourcePath;
        existing.reviewState = node.reviewState;
        existing.updatedByType = node.updatedByType;
        existing.updatedById = node.updatedById;
        existing.updatedAt = new Date(node.updatedAt);
        return cloneContextNode(existing);
      }
      this.contextNodes.set(node.id, cloneContextNode(node));
      return cloneContextNode(node);
    });
  }

  private findContextNodeByScope(node: ContextNodeRecord): ContextNodeRecord | undefined {
    for (const existing of this.contextNodes.values()) {
      if (
        existing.projectId === node.projectId &&
        existing.scopeType === node.scopeType &&
        existing.repoId === node.repoId &&
        existing.path === node.path &&
        existing.taskId === node.taskId
      ) {
        return existing;
      }
    }
    return undefined;
  }

  async listActiveConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const result: ConstraintRecord[] = [];
    for (const constraint of this.constraints.values()) {
      if (constraint.projectId === projectId && constraint.status === "active") {
        result.push(cloneConstraint(constraint));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async listConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const result: ConstraintRecord[] = [];
    for (const constraint of this.constraints.values()) {
      if (constraint.projectId === projectId) {
        result.push(cloneConstraint(constraint));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    return this.enqueueWrite(() => {
      if (this.constraints.has(constraint.id)) {
        throw new UniqueViolationError("constraints_pkey");
      }
      this.constraints.set(constraint.id, cloneConstraint(constraint));
      return cloneConstraint(constraint);
    });
  }

  seedConstraint(constraint: ConstraintRecord): void {
    this.constraints.set(constraint.id, cloneConstraint(constraint));
  }

  private seedDefaultSecurityConstraints(projectId: string, createdAt: Date): void {
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
    return this.enqueueWrite(() => {
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
    });
  }

  async listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]> {
    const result: DecisionRecord[] = [];
    for (const decision of this.decisions.values()) {
      if (decision.projectId === projectId && decision.status === "accepted") {
        result.push(cloneDecision(decision));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  seedDecision(decision: DecisionRecord): void {
    this.decisions.set(decision.id, cloneDecision(decision));
  }

  async insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord> {
    return this.enqueueWrite(() => {
      if (this.contextRevisions.has(revision.id)) {
        throw new UniqueViolationError("context_revisions_pkey");
      }
      this.contextRevisions.set(revision.id, cloneContextRevision(revision));
      return cloneContextRevision(revision);
    });
  }

  async listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]> {
    const result: ContextRevisionRecord[] = [];
    for (const revision of this.contextRevisions.values()) {
      if (revision.projectId === projectId) {
        result.push(cloneContextRevision(revision));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
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
        createDecision: async (decision) => this.insertDecisionUnlocked(decision),
        createConstraint: async (constraint) => this.insertConstraintUnlocked(constraint),
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
  private readonly handoffs = new Map<string, HandoffRecord>();
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
  putAgentSession(session: AgentSessionRef): void {
    this.agentSessions.set(session.id, { ...session });
  }

  async findAgentSessionById(id: string): Promise<AgentSessionRef | undefined> {
    const session = this.agentSessions.get(id);
    return session ? { ...session } : undefined;
  }

  async listAgentSessions(projectId: string): Promise<AgentSessionRecord[]> {
    const result: AgentSessionRecord[] = [];
    for (const session of this.agentSessions.values()) {
      if (session.projectId === projectId) {
        result.push(cloneAgentSession(session));
      }
    }
    result.sort(
      (a, b) => b.startedAt.getTime() - a.startedAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async heartbeatSession(id: string, now: Date): Promise<AgentSessionRecord | undefined> {
    return this.enqueueWrite(() => {
      const session = this.agentSessions.get(id);
      if (!session || session.status !== "active") {
        return session ? cloneAgentSession(session) : undefined;
      }
      session.lastHeartbeatAt = new Date(now);
      session.lockExpiresAt = new Date(now.getTime() + LOCK_TTL_MS);
      if (session.taskId) {
        const task = this.tasks.get(session.taskId);
        if (task && task.lockedBySessionId === session.id) {
          task.lockExpiresAt = new Date(session.lockExpiresAt);
        }
      }
      return cloneAgentSession(session);
    });
  }

  async finishWork(input: FinishWorkInput): Promise<FinishWorkResult | undefined> {
    return this.enqueueWrite(() => this.finishWorkUnlocked(input));
  }

  async findLatestHandoffByTaskId(taskId: string): Promise<HandoffRecord | undefined> {
    let latest: HandoffRecord | undefined;
    for (const handoff of this.handoffs.values()) {
      if (handoff.taskId !== taskId) {
        continue;
      }
      if (
        !latest ||
        handoff.createdAt.getTime() > latest.createdAt.getTime() ||
        (handoff.createdAt.getTime() === latest.createdAt.getTime() && handoff.id > latest.id)
      ) {
        latest = handoff;
      }
    }
    return latest ? cloneHandoff(latest) : undefined;
  }

  private startWorkUnlocked(input: StartWorkInput): StartWorkWriteResult {
    if (this.agentSessions.has(input.session.id)) {
      throw new UniqueViolationError("agent_sessions_pkey");
    }
    if (this.contextRevisions.has(input.revision.id)) {
      throw new UniqueViolationError("context_revisions_pkey");
    }
    if (input.session.tokenId) {
      const token = this.apiTokens.get(input.session.tokenId);
      if (!token || token.projectId !== input.session.projectId) {
        throw new InvalidReferenceError("token");
      }
    }

    let stolenFrom: string | null = null;
    if (input.session.taskId) {
      const task = this.tasks.get(input.session.taskId);
      if (!task || task.deletedAt || task.projectId !== input.session.projectId) {
        throw new InvalidReferenceError("task");
      }
      if (task.lockedBySessionId && task.lockedBySessionId !== input.session.id) {
        if (isLockActive(task, input.now) && !input.steal) {
          throw new TaskLockedError(cloneTask(task));
        }
        stolenFrom = task.lockedBySessionId;
      }
    }

    const revision = cloneContextRevision({ ...input.revision, sessionId: input.session.id });
    this.contextRevisions.set(revision.id, revision);
    this.agentSessions.set(input.session.id, cloneAgentSession(input.session));

    if (input.session.taskId) {
      const task = this.tasks.get(input.session.taskId);
      if (task) {
        if (stolenFrom) {
          const previous = this.agentSessions.get(stolenFrom);
          if (previous && previous.status === "active") {
            previous.status = "abandoned";
            previous.finishedAt = new Date(input.now);
          }
        }
        task.lockedBySessionId = input.session.id;
        task.lockExpiresAt = input.session.lockExpiresAt
          ? new Date(input.session.lockExpiresAt)
          : null;
        task.updatedAt = new Date(input.now);
      }
    }
    return { session: cloneAgentSession(input.session), stolenFrom };
  }

  private finishWorkUnlocked(input: FinishWorkInput): FinishWorkResult | undefined {
    const session = this.agentSessions.get(input.sessionId);
    if (!session) {
      return undefined;
    }
    if (session.status !== "active") {
      throw new SessionNotActiveError(cloneAgentSession(session));
    }
    if (this.handoffs.has(input.handoffId)) {
      throw new UniqueViolationError("handoffs_pkey");
    }

    let task: TaskRecord | null = null;
    let lockReleased = false;
    let previousStatus: TaskRecord["status"] | null = null;
    if (session.taskId) {
      const current = this.tasks.get(session.taskId);
      if (current && !current.deletedAt) {
        if (
          current.lockedBySessionId &&
          current.lockedBySessionId !== session.id &&
          isLockActive(current, input.now)
        ) {
          throw new TaskLockedError(cloneTask(current));
        }
        previousStatus = current.status;
        current.status = input.taskStatus;
        lockReleased = current.lockedBySessionId === session.id;
        if (lockReleased) {
          current.lockedBySessionId = null;
          current.lockExpiresAt = null;
        }
        current.version += 1;
        current.updatedAt = new Date(input.now);
        task = cloneTask(current);
      }
    }

    const handoff: HandoffRecord = {
      id: input.handoffId,
      sessionId: session.id,
      taskId: session.taskId,
      summary: input.summary,
      nextSteps: input.nextSteps,
      filesTouched: input.filesTouched.map((path) => ({ ...path })),
      openQuestions: [...input.openQuestions],
      createdAt: new Date(input.now),
    };
    this.handoffs.set(handoff.id, cloneHandoff(handoff));

    session.status = "finished";
    session.finishedAt = new Date(input.now);

    return {
      session: cloneAgentSession(session),
      handoff: cloneHandoff(handoff),
      task,
      lockReleased,
      previousStatus,
    };
  }

  async findConstraintById(id: string): Promise<ConstraintRecord | undefined> {
    const constraint = this.constraints.get(id);
    return constraint ? cloneConstraint(constraint) : undefined;
  }

  async createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    return this.enqueueWrite(() => this.insertConstraintUnlocked(constraint));
  }

  async applyConstraint(id: string, appliedAt: Date): Promise<ConstraintRecord | undefined> {
    return this.enqueueWrite(() => {
      const constraint = this.constraints.get(id);
      if (!constraint || constraint.status !== "proposed") {
        return undefined;
      }
      constraint.status = "active";
      void appliedAt;
      return cloneConstraint(constraint);
    });
  }

  async listDecisions(projectId: string): Promise<DecisionRecord[]> {
    const result: DecisionRecord[] = [];
    for (const decision of this.decisions.values()) {
      if (decision.projectId === projectId) {
        result.push(cloneDecision(decision));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async findDecisionById(id: string): Promise<DecisionRecord | undefined> {
    const decision = this.decisions.get(id);
    return decision ? cloneDecision(decision) : undefined;
  }

  async createDecision(decision: DecisionRecord): Promise<DecisionRecord> {
    return this.enqueueWrite(() => this.insertDecisionUnlocked(decision));
  }

  async findProjectRepo(id: string): Promise<ProjectRepoRef | undefined> {
    const repo = this.projectRepos.get(id);
    return repo ? { ...repo } : undefined;
  }

  private insertConstraintUnlocked(constraint: ConstraintRecord): ConstraintRecord {
    if (this.constraints.has(constraint.id)) {
      throw new UniqueViolationError("constraints_pkey");
    }
    this.constraints.set(constraint.id, cloneConstraint(constraint));
    return cloneConstraint(constraint);
  }

  private insertDecisionUnlocked(decision: DecisionRecord): DecisionRecord {
    if (this.decisions.has(decision.id)) {
      throw new UniqueViolationError("decisions_pkey");
    }
    const seenPaths = new Set<string>();
    for (const path of decision.relatedPaths) {
      const key = `${path.repoId}:${path.path}`;
      if (seenPaths.has(key)) {
        throw new UniqueViolationError("decision_paths_decision_id_repo_id_path_pk");
      }
      seenPaths.add(key);
    }
    const seenTasks = new Set<string>();
    for (const taskId of decision.relatedTaskIds) {
      if (seenTasks.has(taskId)) {
        throw new UniqueViolationError("decision_tasks_decision_id_task_id_pk");
      }
      seenTasks.add(taskId);
    }
    this.decisions.set(decision.id, cloneDecision(decision));
    return cloneDecision(decision);
  }
    string,
    { response: unknown; createdAt: Date }
  >();
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

function cloneContextNode(node: ContextNodeRecord): ContextNodeRecord {
  return {
    ...node,
    sections: node.sections.map((section) => ({ ...section })),
    updatedAt: new Date(node.updatedAt),
  };
}

function cloneConstraint(constraint: ConstraintRecord): ConstraintRecord {
  return { ...constraint, createdAt: new Date(constraint.createdAt) };
}

function cloneDecision(decision: DecisionRecord): DecisionRecord {
  return {
    ...decision,
    relatedPaths: decision.relatedPaths.map((path) => ({ ...path })),
    relatedTaskIds: [...decision.relatedTaskIds],
    createdAt: new Date(decision.createdAt),
  };
}

function cloneContextRevision(revision: ContextRevisionRecord): ContextRevisionRecord {
  return {
    ...revision,
    target: { ...revision.target },
    briefJson: structuredClone(revision.briefJson),
    sourceNodeIds: [...revision.sourceNodeIds],
    createdAt: new Date(revision.createdAt),
  };
}

function cloneProjectRepo(repo: ProjectRepoRecord): ProjectRepoRecord {
  return {
    ...repo,
    lastIndexedAt: repo.lastIndexedAt ? new Date(repo.lastIndexedAt) : null,
  };
}

function cloneCodeOwner(row: CodeOwnerRecord): CodeOwnerRecord {
  return { ...row, owners: [...row.owners] };
}

function idempotencyKey(actorType: IdempotencyActorType, actorId: string, key: string): string {
  return `${actorType}:${actorId}:${key}`;
}

function cloneAgentSession(session: AgentSessionRecord): AgentSessionRecord {
  return {
    ...session,
    startedAt: new Date(session.startedAt),
    finishedAt: session.finishedAt ? new Date(session.finishedAt) : null,
    lockExpiresAt: session.lockExpiresAt ? new Date(session.lockExpiresAt) : null,
    lastHeartbeatAt: new Date(session.lastHeartbeatAt),
  };
}

function cloneHandoff(handoff: HandoffRecord): HandoffRecord {
  return {
    ...handoff,
    filesTouched: handoff.filesTouched.map((path) => ({ ...path })),
    openQuestions: [...handoff.openQuestions],
    createdAt: new Date(handoff.createdAt),
  };
}

export type ProjectRepoRef = {
  id: string;
  projectId: string;
};
