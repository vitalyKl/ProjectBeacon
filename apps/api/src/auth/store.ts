import { AsyncLocalStorage } from "node:async_hooks";
import {
  DEFAULT_PROJECT_LABELS,
  DEFAULT_PROJECT_LABEL_STATUS,
  DEFAULT_SECURITY_CONSTRAINTS,
  DEFAULT_SECURITY_CONSTRAINT_KIND,
  DEFAULT_SECURITY_CONSTRAINT_STATUS,
} from "@beacon/context";
import { uuidv7 } from "@beacon/shared";
import type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionPatch, DecisionRecord, ProjectRepoRecord } from "../context/types.js";
import type { LabelPatch, LabelRecord } from "../labels/types.js";
import { slugCandidate, slugFromLogin } from "../slug.js";
import { higherOrgRole, higherProjectRole, OrgSlugTakenError, ProjectSlugTakenError, type OrgInviteRecord, type OrgMemberRecord, type OrgRecord, type ProjectInviteRecord, type ProjectMemberRecord, type ProjectRecord } from "../orgs/types.js";
import { wouldCreateCycle } from "../roadmap/cycle.js";
import { applyTaskPatch } from "../roadmap/patch.js";
import { DependencyCycleError, IDEMPOTENCY_TTL_MS, VersionConflictError, type ActivityEventRecord, type IdempotencyActorType, type MilestoneRecord, type TaskCommentRecord, type TaskDependencyRecord, type TaskPatch, type TaskRecord } from "../roadmap/types.js";
import type { AgentSessionRef, ApprovalRecord, RateBucketRecord, TokenRecord } from "../tokens/types.js";
export type { OrgInviteRecord, OrgMemberRecord, OrgRecord, ProjectInviteRecord, ProjectMemberRecord, ProjectRecord } from "../orgs/types.js";
export { InviteTargetRequiredError, OrgSlugTakenError, ProjectSlugTakenError } from "../orgs/types.js";
export { DependencyCycleError, VersionConflictError } from "../roadmap/types.js";
export type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionPatch, DecisionRecord, ProjectRepoRecord, DecisionPathLink } from "../context/types.js";
export type { LabelPatch, LabelRecord } from "../labels/types.js";
export type { ActivityEventRecord, MilestoneRecord, TaskCommentRecord, TaskDependencyRecord, TaskPatch, TaskRecord } from "../roadmap/types.js";
export type { AgentSessionRef, ApprovalRecord, RateBucketRecord, TokenRecord } from "../tokens/types.js";
import { InvalidReferenceError, isLockActive, LOCK_TTL_MS, SessionNotActiveError, TaskLockedError, finishWorkActivities, type AgentSessionRecord, type FinishWorkInput, type FinishWorkResult, type HandoffRecord, type StartWorkInput, type StartWorkWriteResult } from "../sessions/types.js";
import type { ProjectReportRecord, ProjectReviewRecord } from "../reports/types.js";
import { cloneReport, cloneReview } from "../reports/build.js";
export type { AgentSessionRecord, FinishWorkResult, HandoffRecord } from "../sessions/types.js";
export { InvalidReferenceError, SessionNotActiveError, TaskLockedError } from "../sessions/types.js";
import { ACTIVITY_RETENTION_MS, BRIEF_RETENTION_PER_PROJECT, IDEMPOTENCY_RETENTION_MS, idsOlderThanKeep, isUserSessionPastRetention, type ExpireLocksCounts, type RetentionCounts, decideExpiredLocks } from "../jobs/policy.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../github/types.js";
import type { ContextStore, ImportContextInput, ImportContextResult } from "../context/store.js";
import type { GithubStore } from "../github/store.js";
import type { JobStore } from "../jobs/store.js";
import type { OrgStore } from "../orgs/store.js";
import type { ReportStore } from "../reports/store.js";
import type { ProjectRepoRef, RepoStore } from "../repos/store.js";
import type { RoadmapStore } from "../roadmap/store.js";
import type { WorkStore } from "../sessions/store.js";
import type { TokenStore } from "../tokens/store.js";
import {
  BootstrapConsumedError,
  GithubIdTakenError,
  LoginTakenError,
  type IdentityStore,
  type SessionRecord,
  type UserRecord,
} from "./identity.js";

export type { SessionRecord, UserRecord } from "./identity.js";
export { BootstrapConsumedError, GithubIdTakenError, LoginTakenError } from "./identity.js";
export type { ProjectRepoRef } from "../repos/store.js";

export class UniqueViolationError extends Error {
  override readonly name = "UniqueViolationError";

  constructor(constraint: string) {
    super(`unique constraint violated: ${constraint}`);
  }
}

export type AuthStore = IdentityStore &
  OrgStore &
  TokenStore &
  RoadmapStore &
  ContextStore &
  WorkStore &
  RepoStore &
  GithubStore &
  ReportStore &
  JobStore;

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
  private readonly labels = new Map<string, LabelRecord>();
  private readonly taskLabelIds = new Map<string, string[]>();
  private readonly contextRevisions = new Map<string, ContextRevisionRecord>();
  private readonly projectRepos = new Map<string, ProjectRepoRecord>();
  private readonly codeOwners = new Map<string, CodeOwnerRecord>();
  private readonly idempotency = new Map<string, { response: unknown; createdAt: Date }>();
  private readonly agentSessions = new Map<string, AgentSessionRecord>();
  private writeTail: Promise<void> = Promise.resolve();
  private readonly writeContext = new AsyncLocalStorage<true>();

  private orgMemberKey(orgId: string, userId: string): string {
    return `${orgId}:${userId}`;
  }

  private projectMemberKey(projectId: string, userId: string): string {
    return `${projectId}:${userId}`;
  }

  private enqueueWrite<T>(fn: () => T | Promise<T>): Promise<T> {
    if (this.writeContext.getStore()) {
      return Promise.resolve().then(fn);
    }
    const run = this.writeTail.then(() => this.writeContext.run(true, fn));
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

  async createTask(task: TaskRecord, labelIds: string[] = []): Promise<TaskRecord> {
    return this.enqueueWrite(() => {
      const created = this.insertTaskUnlocked(task);
      if (labelIds.length > 0) {
        this.replaceTaskLabelsUnlocked(task.id, labelIds);
      }
      return created;
    });
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
      const next = applyTaskPatch(task, patch);
      if (next.githubIssueId !== null) {
        for (const existing of this.tasks.values()) {
          if (
            existing.id !== task.id &&
            existing.projectId === task.projectId &&
            existing.githubIssueId === next.githubIssueId &&
            !existing.deletedAt
          ) {
            throw new UniqueViolationError("tasks_project_github_issue_id_unique");
          }
        }
      }
      const lockReleased = Boolean(options?.releaseLock && task.lockedBySessionId);
      Object.assign(task, next);
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

  async findContextNodeById(id: string): Promise<ContextNodeRecord | undefined> {
    const node = this.contextNodes.get(id);
    return node ? cloneContextNode(node) : undefined;
  }

  async findContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): Promise<ContextNodeRecord | undefined> {
    const node = this.matchContextNodeByScope(scope);
    return node ? cloneContextNode(node) : undefined;
  }

  seedContextNode(node: ContextNodeRecord): void {
    this.contextNodes.set(node.id, cloneContextNode(node));
  }

  seedProjectRepo(repo: ProjectRepoRecord): void {
    this.projectRepos.set(repo.id, cloneProjectRepo(repo));
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

  async upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return this.enqueueWrite(() => this.upsertContextNodeUnlocked(node));
  }

  async importContext(input: ImportContextInput): Promise<ImportContextResult> {
    return this.enqueueWrite(() => {
      const nodes = input.nodes.map((node) => this.upsertContextNodeUnlocked(node));
      let codeOwnersWritten = 0;
      if (input.codeOwners) {
        codeOwnersWritten = this.upsertCodeOwnersUnlocked(
          input.codeOwners.repoId,
          input.codeOwners.rows,
        ).length;
      }
      return { nodes, codeOwnersWritten };
    });
  }

  async insertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return this.enqueueWrite(() => {
      const existing = this.matchContextNodeByScope(node);
      if (existing) {
        return cloneContextNode(existing);
      }
      this.contextNodes.set(node.id, cloneContextNode(node));
      return cloneContextNode(node);
    });
  }

  private matchContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): ContextNodeRecord | undefined {
    for (const existing of this.contextNodes.values()) {
      if (
        existing.projectId === scope.projectId &&
        existing.scopeType === scope.scopeType &&
        existing.repoId === scope.repoId &&
        existing.path === scope.path &&
        existing.taskId === scope.taskId
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
    return this.createConstraint(constraint);
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

  private seedDefaultProjectLabels(projectId: string, createdAt: Date): void {
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

  async backfillEmptyProjectLabelCatalogs(): Promise<number> {
    return this.enqueueWrite(() => {
      let count = 0;
      for (const project of this.projects.values()) {
        if (project.deletedAt) {
          continue;
        }
        const empty = ![...this.labels.values()].some((label) => label.projectId === project.id);
        if (!empty) {
          continue;
        }
        this.seedDefaultProjectLabels(project.id, project.createdAt);
        count += 1;
      }
      return count;
    });
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
    return this.enqueueWrite(() => this.upsertCodeOwnersUnlocked(repoId, rows));
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

  async findContextRevisionById(id: string): Promise<ContextRevisionRecord | undefined> {
    const revision = this.contextRevisions.get(id);
    return revision ? cloneContextRevision(revision) : undefined;
  }

  async withIdempotency<T>(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: () => Promise<T>,
  ): Promise<T> {
    return this.enqueueWrite(async () => {
      const slot = idempotencyKey(actorType, actorId, key);
      const stored = this.idempotency.get(slot);
      if (stored && now.getTime() - stored.createdAt.getTime() <= IDEMPOTENCY_TTL_MS) {
        return structuredClone(stored.response) as T;
      }
      this.idempotency.delete(slot);
      const response = await produce();
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

  private upsertContextNodeUnlocked(node: ContextNodeRecord): ContextNodeRecord {
    const existing = this.matchContextNodeByScope(node);
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
  }

  private upsertCodeOwnersUnlocked(repoId: string, rows: CodeOwnerRecord[]): CodeOwnerRecord[] {
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
    result.sort(
      (a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
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
  putAgentSession(session: AgentSessionRef | AgentSessionRecord): void {
    if ("agentName" in session) {
      this.agentSessions.set(session.id, cloneAgentSession(session));
      return;
    }
    const now = new Date();
    this.agentSessions.set(
      session.id,
      cloneAgentSession({
        id: session.id,
        projectId: session.projectId,
        taskId: null,
        tokenId: null,
        agentName: "test",
        agentHost: "custom",
        status: "active",
        contextRevisionId: null,
        startedAt: now,
        finishedAt: null,
        lockExpiresAt: null,
        lastHeartbeatAt: now,
      }),
    );
  }

  async findAgentSessionById(id: string): Promise<AgentSessionRecord | undefined> {
    const session = this.agentSessions.get(id);
    return session ? cloneAgentSession(session) : undefined;
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

  async startWork(input: StartWorkInput): Promise<StartWorkWriteResult> {
    return this.enqueueWrite(() => this.startWorkUnlocked(input));
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
        if (input.howToCheck.trim().length > 0) {
          current.howToCheck = input.howToCheck;
        }
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

    const finished = {
      session: cloneAgentSession(session),
      handoff: cloneHandoff(handoff),
      task,
      lockReleased,
      previousStatus,
    };
    for (const event of finishWorkActivities({
      session: finished.session,
      task: finished.task,
      previousStatus: finished.previousStatus,
      lockReleased: finished.lockReleased,
      taskStatus: input.taskStatus,
      actorType: input.actorType,
      actorId: input.actorId,
      now: input.now,
    })) {
      this.insertActivityUnlocked(event);
    }
    return finished;
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

  async listLabels(projectId: string): Promise<LabelRecord[]> {
    const result: LabelRecord[] = [];
    for (const label of this.labels.values()) {
      if (label.projectId === projectId) {
        result.push(cloneLabel(label));
      }
    }
    result.sort(
      (a, b) =>
        a.status.localeCompare(b.status) ||
        a.name.localeCompare(b.name) ||
        a.id.localeCompare(b.id),
    );
    return result;
  }

  async findLabelById(id: string): Promise<LabelRecord | undefined> {
    const label = this.labels.get(id);
    return label ? cloneLabel(label) : undefined;
  }

  async createLabel(label: LabelRecord): Promise<LabelRecord> {
    return this.enqueueWrite(() => {
      for (const existing of this.labels.values()) {
        if (existing.projectId === label.projectId && existing.slug === label.slug) {
          throw new UniqueViolationError("labels_project_slug");
        }
      }
      this.labels.set(label.id, cloneLabel(label));
      return cloneLabel(label);
    });
  }

  async updateLabel(id: string, patch: LabelPatch): Promise<LabelRecord | undefined> {
    return this.enqueueWrite(() => {
      const label = this.labels.get(id);
      if (!label) {
        return undefined;
      }
      if (patch.slug !== undefined && patch.slug !== label.slug) {
        for (const existing of this.labels.values()) {
          if (
            existing.id !== id &&
            existing.projectId === label.projectId &&
            existing.slug === patch.slug
          ) {
            throw new UniqueViolationError("labels_project_slug");
          }
        }
        label.slug = patch.slug;
      }
      if (patch.name !== undefined) {
        label.name = patch.name;
      }
      if (patch.description !== undefined) {
        label.description = patch.description;
      }
      if (patch.color !== undefined) {
        label.color = patch.color;
      }
      if (patch.status !== undefined) {
        label.status = patch.status;
      }
      if (patch.paths !== undefined) {
        label.paths = patch.paths.map((path) => ({ ...path }));
      }
      return cloneLabel(label);
    });
  }

  async listTaskLabels(taskId: string): Promise<LabelRecord[]> {
    const ids = this.taskLabelIds.get(taskId) ?? [];
    const result: LabelRecord[] = [];
    for (const id of ids) {
      const label = this.labels.get(id);
      if (label) {
        result.push(cloneLabel(label));
      }
    }
    result.sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    return result;
  }

  async setTaskLabels(taskId: string, labelIds: string[]): Promise<LabelRecord[]> {
    return this.enqueueWrite(() => this.replaceTaskLabelsUnlocked(taskId, labelIds));
  }

  private replaceTaskLabelsUnlocked(taskId: string, labelIds: string[]): LabelRecord[] {
    const unique: string[] = [];
    const seen = new Set<string>();
    for (const id of labelIds) {
      if (seen.has(id)) {
        continue;
      }
      seen.add(id);
      unique.push(id);
    }
    this.taskLabelIds.set(taskId, unique);
    const result: LabelRecord[] = [];
    for (const id of unique) {
      const label = this.labels.get(id);
      if (label) {
        result.push(cloneLabel(label));
      }
    }
    result.sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    return result;
  }

  async findDecisionById(id: string): Promise<DecisionRecord | undefined> {
    const decision = this.decisions.get(id);
    return decision ? cloneDecision(decision) : undefined;
  }

  async createDecision(decision: DecisionRecord): Promise<DecisionRecord> {
    return this.enqueueWrite(() => this.insertDecisionUnlocked(decision));
  }

  async updateDecision(id: string, patch: DecisionPatch): Promise<DecisionRecord | undefined> {
    return this.enqueueWrite(() => {
      const decision = this.decisions.get(id);
      if (!decision) {
        return undefined;
      }
      decision.status = patch.status;
      if (patch.supersededBy !== undefined) {
        decision.supersededBy = patch.supersededBy;
      }
      return cloneDecision(decision);
    });
  }

  async findProjectRepo(id: string): Promise<ProjectRepoRef | undefined> {
    const repo = await this.findProjectRepoById(id);
    return repo ? { id: repo.id, projectId: repo.projectId } : undefined;
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

  async listComments(taskId: string): Promise<TaskCommentRecord[]> {
    const result: TaskCommentRecord[] = [];
    for (const comment of this.comments.values()) {
      if (comment.taskId === taskId) {
        result.push(cloneComment(comment));
      }
    }
    result.sort(
      (a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
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
  seedContextRevision(revision: ContextRevisionRecord): void {
    this.contextRevisions.set(revision.id, cloneContextRevision(revision));
  }
  seedActivity(event: ActivityEventRecord): void {
    this.activity.set(event.id, cloneActivity(event));
  }
  seedIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    createdAt: Date,
    response: unknown = {},
  ): void {
    this.idempotency.set(idempotencyKey(actorType, actorId, key), {
      response: structuredClone(response),
      createdAt: new Date(createdAt),
    });
  }

  async runRetention(now: Date): Promise<RetentionCounts> {
    return this.enqueueWrite(() => {
      const activityCutoff = now.getTime() - ACTIVITY_RETENTION_MS;
      let activityDeleted = 0;
      for (const [id, event] of this.activity) {
        if (event.createdAt.getTime() < activityCutoff) {
          this.activity.delete(id);
          activityDeleted += 1;
        }
      }

      const byProject = new Map<string, ContextRevisionRecord[]>();
      for (const revision of this.contextRevisions.values()) {
        const list = byProject.get(revision.projectId) ?? [];
        list.push(revision);
        byProject.set(revision.projectId, list);
      }
      let briefsDeleted = 0;
      for (const revisions of byProject.values()) {
        for (const id of idsOlderThanKeep(revisions, BRIEF_RETENTION_PER_PROJECT)) {
          for (const session of this.agentSessions.values()) {
            if (session.contextRevisionId === id) {
              session.contextRevisionId = null;
            }
          }
          this.contextRevisions.delete(id);
          briefsDeleted += 1;
        }
      }

      const idempotencyCutoff = now.getTime() - IDEMPOTENCY_RETENTION_MS;
      let idempotencyDeleted = 0;
      for (const [key, slot] of this.idempotency) {
        if (slot.createdAt.getTime() < idempotencyCutoff) {
          this.idempotency.delete(key);
          idempotencyDeleted += 1;
        }
      }

      let sessionsDeleted = 0;
      for (const [id, session] of this.sessions) {
        if (isUserSessionPastRetention(session, now)) {
          this.sessions.delete(id);
          sessionsDeleted += 1;
        }
      }

      return {
        activityDeleted,
        briefsDeleted,
        idempotencyDeleted,
        sessionsDeleted,
      };
    });
  }

  async expireLocks(now: Date): Promise<ExpireLocksCounts> {
    return this.enqueueWrite(() => this.expireLocksUnlocked(now));
  }

  private expireLocksUnlocked(now: Date): ExpireLocksCounts {
    const decided = decideExpiredLocks(
      [...this.agentSessions.values()],
      [...this.tasks.values()],
      now,
    );
    for (const sessionId of decided.sessionIds) {
      const session = this.agentSessions.get(sessionId);
      if (!session || session.status !== "active") {
        continue;
      }
      session.status = "abandoned";
      session.finishedAt = new Date(now);
    }
    for (const taskId of decided.taskIds) {
      const task = this.tasks.get(taskId);
      if (!task) {
        continue;
      }
      task.lockedBySessionId = null;
      task.lockExpiresAt = null;
    }
    return {
      locksReleased: decided.taskIds.length,
      sessionsAbandoned: decided.sessionIds.length,
    };
  }
  private readonly githubInstallations = new Map<string, GithubInstallationRecord>();
  private readonly githubSyncState = new Map<string, GithubSyncStateRecord>();

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

  async listDependencies(
    projectId: string,
  ): Promise<(TaskDependencyRecord & { createdAt: Date })[]> {
    const result: (TaskDependencyRecord & { createdAt: Date })[] = [];
    for (const dependency of this.dependencies) {
      const from = this.tasks.get(dependency.fromTaskId);
      const to = this.tasks.get(dependency.toTaskId);
      if (!from || !to || from.deletedAt || to.deletedAt) {
        continue;
      }
      if (from.projectId !== projectId || to.projectId !== projectId) {
        continue;
      }
      result.push({
        ...dependency,
        createdAt: new Date(from.createdAt),
      });
    }
    return result;
  }
  private readonly sidecarConnections = new Map<
    string,
    { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date }
  >();
  private readonly cloneInvalidations = new Map<
    string,
    { id: string; repoId: string; sha: string | null; createdAt: Date; consumedAt: Date | null }
  >();

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

  async listSidecarConnections(): Promise<
    Array<{ id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date }>
  > {
    return [...this.sidecarConnections.values()].map((row) => ({
      ...row,
      connectedAt: new Date(row.connectedAt),
      lastSeenAt: new Date(row.lastSeenAt),
    }));
  }

  async countPendingApprovals(): Promise<number> {
    let count = 0;
    for (const approval of this.approvals.values()) {
      if (approval.status === "pending") {
        count += 1;
      }
    }
    return count;
  }

  async githubSyncLagSeconds(now: Date): Promise<number> {
    void now;
    return 0;
  }

  private readonly reports = new Map<string, ProjectReportRecord>();
  private readonly reviews = new Map<string, ProjectReviewRecord>();

  async createReport(report: ProjectReportRecord): Promise<ProjectReportRecord> {
    this.reports.set(report.id, cloneReport(report));
    return cloneReport(report);
  }

  async listReports(projectId: string): Promise<ProjectReportRecord[]> {
    return [...this.reports.values()]
      .filter((item) => item.projectId === projectId)
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id))
      .map(cloneReport);
  }

  async findReportById(id: string): Promise<ProjectReportRecord | undefined> {
    const report = this.reports.get(id);
    return report ? cloneReport(report) : undefined;
  }

  async createReview(review: ProjectReviewRecord): Promise<ProjectReviewRecord> {
    this.reviews.set(review.id, cloneReview(review));
    return cloneReview(review);
  }

  async listReviews(projectId: string): Promise<ProjectReviewRecord[]> {
    return [...this.reviews.values()]
      .filter((item) => item.projectId === projectId)
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id))
      .map(cloneReview);
  }

  async findReviewById(id: string): Promise<ProjectReviewRecord | undefined> {
    const review = this.reviews.get(id);
    return review ? cloneReview(review) : undefined;
  }
}

function cloneLabel(label: LabelRecord): LabelRecord {
  return {
    ...label,
    paths: label.paths.map((path) => ({ ...path })),
    createdAt: new Date(label.createdAt),
  };
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

export class ProjectNotFoundError extends Error {
  override readonly name = "ProjectNotFoundError";

  constructor() {
    super("project not found");
  }
}

function cloneGithubInstallation(row: GithubInstallationRecord): GithubInstallationRecord {
  return { ...row, createdAt: new Date(row.createdAt) };
}

function cloneGithubSyncState(row: GithubSyncStateRecord): GithubSyncStateRecord {
  return {
    ...row,
    lastSyncedAt: row.lastSyncedAt ? new Date(row.lastSyncedAt) : null,
  };
}

function cloneSidecar(row: {
  id: string;
  repoId: string;
  tokenId: string;
  connectedAt: Date;
  lastSeenAt: Date;
}): { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date } {
  return {
    ...row,
    connectedAt: new Date(row.connectedAt),
    lastSeenAt: new Date(row.lastSeenAt),
  };
}
