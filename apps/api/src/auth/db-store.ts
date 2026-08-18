import {
  DEFAULT_SECURITY_CONSTRAINTS,
  DEFAULT_SECURITY_CONSTRAINT_KIND,
  DEFAULT_SECURITY_CONSTRAINT_STATUS,
} from "@beacon/context";
import {
  activityEvents,
  agentSessions,
  apiTokens,
  approvalRequests,
  codeOwners,
  constraints,
  contextNodes,
  contextRevisions,
  decisionPaths,
  decisions,
  idempotencyKeys,
  milestones,
  orgInvites,
  orgMembers,
  orgs,
  projectInvites,
  projectMembers,
  projectRepos,
  projects,
  rateBuckets,
  taskComments,
  taskDependencies,
  tasks,
  userSessions,
  users,
  type Db,
} from "@beacon/db";
import { isScope, uuidv7, type Scope } from "@beacon/shared";
import { and, asc, desc, eq, inArray, isNull, sql } from "drizzle-orm";

import { slugCandidate, slugFromLogin } from "../slug.js";
import {
  higherOrgRole,
  higherProjectRole,
  type OrgInviteRecord,
  type OrgInviteRole,
  type OrgKind,
  type OrgMemberRecord,
  type OrgRecord,
  type OrgRole,
  type ProjectInviteRecord,
  type ProjectMemberRecord,
  type ProjectRecord,
  type ProjectRole,
} from "../orgs/types.js";
import type { ContextSection } from "@beacon/api-spec";

import {
  isConstraintKind,
  isConstraintStatus,
  isContextScopeType,
  isDecisionStatus,
  type CodeOwnerRecord,
  type ConstraintRecord,
  type ContextNodeRecord,
  type ContextRevisionRecord,
  type ContextRevisionTarget,
  type DecisionRecord,
  type ProjectRepoRecord,
} from "../context/types.js";
import {
  BootstrapConsumedError,
  DependencyCycleError,
  GithubIdTakenError,
  LoginTakenError,
  OrgSlugTakenError,
  ProjectSlugTakenError,
  VersionConflictError,
  type ActivityEventRecord,
  type AuthStore,
  type AgentSessionRef,
  type IdempotentWrites,
  type MilestoneRecord,
  type SessionRecord,
  type TaskCommentRecord,
  type TaskDependencyRecord,
  type TaskPatch,
  type ApprovalRecord,
  type RateBucketRecord,
  type TaskRecord,
  type TokenRecord,
  type UserRecord,
} from "./store.js";
import type { ApprovalStatus } from "../tokens/types.js";
import { wouldCreateCycle } from "../roadmap/cycle.js";
import {
  IDEMPOTENCY_TTL_MS,
  type CommentAuthorType,
  type DependencyType,
  type IdempotencyActorType,
  type LinkedPath,
  type MilestoneStatus,
  type TaskStatus,
  type TaskType,
} from "../roadmap/types.js";

const BOOTSTRAP_LOCK_KEY = 8_811_201;
const IDEMPOTENCY_LOCK_NS = 8_811_202;
const DEPENDENCY_LOCK_NS = 8_811_203;

type UniqueConstraint = "login" | "github_id" | "org_slug" | "project_slug" | "unknown";

function uniqueConstraint(error: unknown): UniqueConstraint | undefined {
  let current: unknown = error;
  for (let i = 0; i < 4 && current; i += 1) {
    if (typeof current === "object" && current !== null && "code" in current) {
      const code = (current as { code: unknown }).code;
      if (code === "23505") {
        const constraint =
          "constraint_name" in current && typeof current.constraint_name === "string"
            ? current.constraint_name
            : "constraint" in current && typeof current.constraint === "string"
              ? current.constraint
              : "";
        if (constraint.includes("login")) {
          return "login";
        }
        if (constraint.includes("github_id")) {
          return "github_id";
        }
        if (constraint.includes("orgs_slug")) {
          return "org_slug";
        }
        if (constraint.includes("projects_org_id_slug")) {
          return "project_slug";
        }
        return "unknown";
      }
    }
    current =
      typeof current === "object" && current !== null && "cause" in current
        ? (current as { cause: unknown }).cause
        : undefined;
  }
  return undefined;
}

function mapUserInsertError(error: unknown): never {
  const constraint = uniqueConstraint(error);
  if (constraint === "login") {
    throw new LoginTakenError();
  }
  if (constraint === "github_id") {
    throw new GithubIdTakenError();
  }
  throw error;
}

function asBuffer(value: Buffer | Uint8Array): Buffer {
  return Buffer.isBuffer(value) ? value : Buffer.from(value);
}

function toUser(row: typeof users.$inferSelect): UserRecord {
  return {
    id: row.id,
    githubId: row.githubId,
    login: row.login,
    email: row.email,
    name: row.name,
    avatarUrl: row.avatarUrl,
    passwordHash: row.passwordHash,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toOrg(row: typeof orgs.$inferSelect): OrgRecord {
  return {
    id: row.id,
    slug: row.slug,
    name: row.name,
    kind: row.kind as OrgKind,
    createdAt: row.createdAt,
  };
}

function toOrgMember(row: typeof orgMembers.$inferSelect): OrgMemberRecord {
  return {
    orgId: row.orgId,
    userId: row.userId,
    role: row.role as OrgRole,
  };
}

function toOrgInvite(row: typeof orgInvites.$inferSelect): OrgInviteRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    email: row.email,
    githubLogin: row.githubLogin,
    role: row.role as OrgInviteRole,
    expiresAt: row.expiresAt,
    acceptedAt: row.acceptedAt,
  };
}

function asSettings(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function toProject(row: typeof projects.$inferSelect): ProjectRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    slug: row.slug,
    name: row.name,
    description: row.description,
    visibility: "private",
    settings: asSettings(row.settings),
    deletedAt: row.deletedAt,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toProjectMember(row: typeof projectMembers.$inferSelect): ProjectMemberRecord {
  return {
    projectId: row.projectId,
    userId: row.userId,
    role: row.role as ProjectRole,
    createdAt: row.createdAt,
  };
}

function toProjectInvite(row: typeof projectInvites.$inferSelect): ProjectInviteRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    email: row.email,
    githubLogin: row.githubLogin,
    role: row.role as ProjectRole,
    invitedBy: row.invitedBy,
    expiresAt: row.expiresAt,
    acceptedAt: row.acceptedAt,
  };
}

function asLinkedPaths(value: unknown): LinkedPath[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const paths: LinkedPath[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      continue;
    }
    const record = item as Record<string, unknown>;
    if (typeof record["repo_id"] === "string" && typeof record["path"] === "string") {
      paths.push({ repo_id: record["repo_id"], path: record["path"] });
    }
  }
  return paths;
}

function asPayload(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function toMilestone(row: typeof milestones.$inferSelect): MilestoneRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    title: row.title,
    description: row.description,
    status: row.status as MilestoneStatus,
    targetDate: row.targetDate,
    sortOrder: row.sortOrder,
    createdAt: row.createdAt,
  };
}

function toTask(row: typeof tasks.$inferSelect): TaskRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    milestoneId: row.milestoneId,
    parentId: row.parentId,
    title: row.title,
    description: row.description,
    status: row.status as TaskStatus,
    priority: row.priority,
    type: row.type as TaskType,
    version: row.version,
    assigneeUserId: row.assigneeUserId,
    assigneeAgentName: row.assigneeAgentName,
    agentBrief: row.agentBrief,
    linkedPaths: asLinkedPaths(row.linkedPaths),
    githubIssueId: row.githubIssueId,
    lockedBySessionId: row.lockedBySessionId,
    lockExpiresAt: row.lockExpiresAt,
    deletedAt: row.deletedAt,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toComment(row: typeof taskComments.$inferSelect): TaskCommentRecord {
  return {
    id: row.id,
    taskId: row.taskId,
    authorType: row.authorType as CommentAuthorType,
    authorId: row.authorId,
    body: row.body,
    createdAt: row.createdAt,
  };
}

function toDependency(row: typeof taskDependencies.$inferSelect): TaskDependencyRecord {
  return {
    fromTaskId: row.fromTaskId,
    toTaskId: row.toTaskId,
    type: row.type as DependencyType,
  };
}

function toActivity(row: typeof activityEvents.$inferSelect): ActivityEventRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    objectType: row.objectType,
    objectId: row.objectId,
    actorType: row.actorType,
    actorId: row.actorId,
    verb: row.verb,
    payload: asPayload(row.payload),
    createdAt: row.createdAt,
  };
}

function asSections(value: unknown): ContextSection[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const sections: ContextSection[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      continue;
    }
    const record = item as Record<string, unknown>;
    const id = record["id"];
    const title = record["title"];
    const body = record["body_md"];
    const ordinal = record["ordinal"];
    if (typeof id !== "string" || typeof title !== "string" || typeof body !== "string") {
      continue;
    }
    if (typeof ordinal !== "number" || !Number.isInteger(ordinal)) {
      continue;
    }
    if (id === "custom") {
      const key = record["key"];
      if (typeof key !== "string" || key.length === 0) {
        continue;
      }
      sections.push({ id, key, title, body_md: body, ordinal });
      continue;
    }
    if (
      id === "goals" ||
      id === "non_goals" ||
      id === "architecture" ||
      id === "conventions" ||
      id === "glossary" ||
      id === "ownership" ||
      id === "pitfalls" ||
      id === "commands" ||
      id === "stack" ||
      id === "security" ||
      id === "style"
    ) {
      const key = record["key"];
      sections.push({
        id,
        ...(typeof key === "string" && key.length > 0 ? { key } : {}),
        title,
        body_md: body,
        ordinal,
      });
    }
  }
  return sections;
}

function toContextNode(row: typeof contextNodes.$inferSelect): ContextNodeRecord | undefined {
  if (!isContextScopeType(row.scopeType)) {
    return undefined;
  }
  return {
    id: row.id,
    projectId: row.projectId,
    repoId: row.repoId,
    taskId: row.taskId,
    scopeType: row.scopeType,
    path: row.path,
    sections: asSections(row.sections),
    sectionsText: row.sectionsText,
    source: row.source,
    sourcePath: row.sourcePath,
    reviewState: row.reviewState === "needs_review" ? "needs_review" : "reviewed",
    updatedByType: row.updatedByType,
    updatedById: row.updatedById,
    updatedAt: row.updatedAt,
  };
}

function toProjectRepo(row: typeof projectRepos.$inferSelect): ProjectRepoRecord | undefined {
  if (row.provider !== "github" && row.provider !== "local") {
    return undefined;
  }
  if (
    row.indexMode !== "sidecar" &&
    row.indexMode !== "bind_mount" &&
    row.indexMode !== "hosted_clone" &&
    row.indexMode !== "both"
  ) {
    return undefined;
  }
  return {
    id: row.id,
    projectId: row.projectId,
    provider: row.provider,
    remoteUrl: row.remoteUrl,
    defaultBranch: row.defaultBranch,
    githubRepoId: row.githubRepoId,
    installationId: row.installationId,
    localRootHint: row.localRootHint,
    indexMode: row.indexMode,
    lastIndexedSha: row.lastIndexedSha,
    lastIndexedAt: row.lastIndexedAt,
  };
}

function toCodeOwner(row: typeof codeOwners.$inferSelect): CodeOwnerRecord {
  return {
    id: row.id,
    repoId: row.repoId,
    pathPattern: row.pathPattern,
    owners: [...row.owners],
    source: row.source,
  };
}

async function seedDefaultSecurityConstraints(
  tx: Pick<Db, "select" | "insert">,
  projectId: string,
  createdAt: Date,
): Promise<void> {
  const existing = await tx
    .select({ body: constraints.body })
    .from(constraints)
    .where(
      and(
        eq(constraints.projectId, projectId),
        eq(constraints.kind, DEFAULT_SECURITY_CONSTRAINT_KIND),
        eq(constraints.status, DEFAULT_SECURITY_CONSTRAINT_STATUS),
      ),
    );
  const existingBodies = new Set(existing.map((row) => row.body));
  const values = DEFAULT_SECURITY_CONSTRAINTS.flatMap((body, index) => {
    if (existingBodies.has(body)) {
      return [];
    }
    return [
      {
        id: uuidv7(createdAt.getTime() + index),
        projectId,
        kind: DEFAULT_SECURITY_CONSTRAINT_KIND,
        body,
        scopePath: "",
        status: DEFAULT_SECURITY_CONSTRAINT_STATUS,
        createdAt,
      },
    ];
  });
  if (values.length === 0) {
    return;
  }
  await tx.insert(constraints).values(values);
}

function toConstraint(row: typeof constraints.$inferSelect): ConstraintRecord | undefined {
  if (!isConstraintKind(row.kind) || !isConstraintStatus(row.status)) {
    return undefined;
  }
  return {
    id: row.id,
    projectId: row.projectId,
    kind: row.kind,
    body: row.body,
    scopePath: row.scopePath,
    status: row.status,
    createdAt: row.createdAt,
  };
}

function toDecision(
  row: typeof decisions.$inferSelect,
  relatedPaths: string[],
): DecisionRecord | undefined {
  if (!isDecisionStatus(row.status)) {
    return undefined;
  }
  return {
    id: row.id,
    projectId: row.projectId,
    title: row.title,
    status: row.status,
    context: row.context,
    decision: row.decision,
    consequences: row.consequences,
    createdByType: row.createdByType,
    createdById: row.createdById,
    supersededBy: row.supersededBy,
    createdAt: row.createdAt,
    relatedPaths,
  };
}

function asRevisionTarget(value: unknown): ContextRevisionTarget {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return { repo_id: null, path: "", task_id: null };
  }
  const record = value as Record<string, unknown>;
  return {
    repo_id: typeof record["repo_id"] === "string" ? record["repo_id"] : null,
    path: typeof record["path"] === "string" ? record["path"] : "",
    task_id: typeof record["task_id"] === "string" ? record["task_id"] : null,
  };
}

function asBriefJson(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function toContextRevision(row: typeof contextRevisions.$inferSelect): ContextRevisionRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    compiledHash: row.compiledHash,
    compilerVersion: row.compilerVersion,
    target: asRevisionTarget(row.target),
    briefMarkdown: row.briefMarkdown,
    briefJson: asBriefJson(row.briefJson),
    tokenEstimate: row.tokenEstimate,
    sourceNodeIds: row.sourceNodeIds ?? [],
    sessionId: row.sessionId,
    createdAt: row.createdAt,
  };
}

function toSession(row: typeof userSessions.$inferSelect): SessionRecord {
  return {
    id: row.id,
    userId: row.userId,
    tokenHash: asBuffer(row.tokenHash),
    createdAt: row.createdAt,
    lastSeenAt: row.lastSeenAt,
    expiresAt: row.expiresAt,
    revokedAt: row.revokedAt,
    userAgent: row.userAgent,
    ip: row.ip,
  };
}

function asScopes(value: string[] | null): Scope[] {
  if (!value) {
    return [];
  }
  return value.filter(isScope);
}

function asPayload(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function toToken(row: typeof apiTokens.$inferSelect): TokenRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    name: row.name,
    tokenHash: asBuffer(row.tokenHash),
    prefix: row.prefix,
    scopes: asScopes(row.scopes),
    createdBy: row.createdBy,
    lastUsedAt: row.lastUsedAt,
    expiresAt: row.expiresAt,
    revokedAt: row.revokedAt,
    createdAt: row.createdAt,
  };
}

function toApproval(row: typeof approvalRequests.$inferSelect): ApprovalRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    sessionId: row.sessionId,
    action: row.action,
    payload: asPayload(row.payload),
    status: row.status as ApprovalStatus,
    requestedAt: row.requestedAt,
    resolvedAt: row.resolvedAt,
    resolvedBy: row.resolvedBy,
  };
}

function toRateBucket(row: typeof rateBuckets.$inferSelect): RateBucketRecord {
  return {
    bucketKey: row.bucketKey,
    windowStart: row.windowStart,
    count: row.count,
    bytes: row.bytes,
  };
}

function toSession(row: typeof userSessions.$inferSelect): SessionRecord {
  return {
    id: row.id,
    userId: row.userId,
    tokenHash: asBuffer(row.tokenHash),
    createdAt: row.createdAt,
    lastSeenAt: row.lastSeenAt,
    expiresAt: row.expiresAt,
    revokedAt: row.revokedAt,
    userAgent: row.userAgent,
    ip: row.ip,
  };
}

export class DbAuthStore implements AuthStore {
  constructor(private readonly db: Db) {}

  async hasAnyUser(): Promise<boolean> {
    const row = await this.db.select({ id: users.id }).from(users).limit(1);
    return row.length > 0;
  }

  async findUserById(id: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.id, id)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByLogin(login: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.login, login)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByEmail(email: string): Promise<UserRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(users)
      .where(sql`lower(${users.email}) = lower(${email})`)
      .limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.githubId, githubId)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    try {
      return toUser(await this.insertUser(this.db, user));
    } catch (error) {
      mapUserInsertError(error);
    }
  }

  async createFirstUser(user: UserRecord): Promise<UserRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        await tx.execute(sql`select pg_advisory_xact_lock(${BOOTSTRAP_LOCK_KEY})`);
        const existing = await tx.select({ id: users.id }).from(users).limit(1);
        if (existing.length > 0) {
          throw new BootstrapConsumedError();
        }
        return toUser(await this.insertUser(tx, user));
      });
    } catch (error) {
      if (error instanceof BootstrapConsumedError) {
        throw error;
      }
      if (uniqueConstraint(error)) {
        throw new BootstrapConsumedError();
      }
      throw error;
    }
  }

  private async insertUser(
    db: Pick<Db, "insert">,
    user: UserRecord,
  ): Promise<typeof users.$inferSelect> {
    const [row] = await db
      .insert(users)
      .values({
        id: user.id,
        githubId: user.githubId,
        login: user.login,
        email: user.email,
        name: user.name,
        avatarUrl: user.avatarUrl,
        passwordHash: user.passwordHash,
        createdAt: user.createdAt,
        updatedAt: user.updatedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert user returned no row");
    }
    return row;
  }

  async createSession(session: SessionRecord): Promise<SessionRecord> {
    const [row] = await this.db
      .insert(userSessions)
      .values({
        id: session.id,
        userId: session.userId,
        tokenHash: session.tokenHash,
        createdAt: session.createdAt,
        lastSeenAt: session.lastSeenAt,
        expiresAt: session.expiresAt,
        revokedAt: session.revokedAt,
        userAgent: session.userAgent,
        ip: session.ip,
      })
      .returning();
    if (!row) {
      throw new Error("insert session returned no row");
    }
    return toSession(row);
  }

  async findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(userSessions)
      .where(eq(userSessions.tokenHash, tokenHash))
      .limit(1);
    return row ? toSession(row) : undefined;
  }

  async updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ lastSeenAt, expiresAt })
      .where(eq(userSessions.id, id));
  }

  async revokeSession(id: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.id, id), isNull(userSessions.revokedAt)));
  }

  async revokeUserSessions(userId: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.userId, userId), isNull(userSessions.revokedAt)));
  }

  private async findOwnedPersonalOrg(
    db: Pick<Db, "select">,
    userId: string,
  ): Promise<OrgRecord | undefined> {
    const [row] = await db
      .select()
      .from(orgs)
      .where(and(eq(orgs.id, userId), eq(orgs.kind, "personal")))
      .limit(1);
    return row ? toOrg(row) : undefined;
  }

  async ensurePersonalOrg(user: UserRecord, now: Date): Promise<OrgRecord> {
    const existing = await this.findOwnedPersonalOrg(this.db, user.id);
    if (existing) {
      await this.upsertOrgMember({ orgId: existing.id, userId: user.id, role: "owner" });
      return existing;
    }

    const base = slugFromLogin(user.login);
    for (let attempt = 1; attempt < 1000; attempt += 1) {
      const slug = slugCandidate(base, attempt);
      try {
        return await this.db.transaction(async (tx) => {
          const raced = await this.findOwnedPersonalOrg(tx, user.id);
          if (raced) {
            await tx
              .insert(orgMembers)
              .values({ orgId: raced.id, userId: user.id, role: "owner" })
              .onConflictDoUpdate({
                target: [orgMembers.orgId, orgMembers.userId],
                set: { role: "owner" },
              });
            return raced;
          }
          const [org] = await tx
            .insert(orgs)
            .values({
              id: user.id,
              slug,
              name: user.name ?? user.login,
              kind: "personal",
              createdAt: now,
            })
            .returning();
          if (!org) {
            throw new Error("insert personal org returned no row");
          }
          await tx.insert(orgMembers).values({
            orgId: org.id,
            userId: user.id,
            role: "owner",
          });
          return toOrg(org);
        });
      } catch (error) {
        if (!uniqueConstraint(error)) {
          throw error;
        }
        const recovered = await this.findOwnedPersonalOrg(this.db, user.id);
        if (recovered) {
          await this.upsertOrgMember({ orgId: recovered.id, userId: user.id, role: "owner" });
          return recovered;
        }
        if (uniqueConstraint(error) === "org_slug") {
          continue;
        }
        throw error;
      }
    }
    throw new OrgSlugTakenError();
  }

  async listOrgsForUser(userId: string): Promise<OrgRecord[]> {
    const rows = await this.db
      .select({ org: orgs })
      .from(orgMembers)
      .innerJoin(orgs, eq(orgs.id, orgMembers.orgId))
      .where(eq(orgMembers.userId, userId))
      .orderBy(asc(orgs.createdAt), asc(orgs.id));
    return rows.map((row) => toOrg(row.org));
  }

  async findOrgById(id: string): Promise<OrgRecord | undefined> {
    const [row] = await this.db.select().from(orgs).where(eq(orgs.id, id)).limit(1);
    return row ? toOrg(row) : undefined;
  }

  async findOrgBySlug(slug: string): Promise<OrgRecord | undefined> {
    const [row] = await this.db.select().from(orgs).where(eq(orgs.slug, slug)).limit(1);
    return row ? toOrg(row) : undefined;
  }

  async createTeamOrg(org: OrgRecord, ownerUserId: string): Promise<OrgRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        const [created] = await tx
          .insert(orgs)
          .values({
            id: org.id,
            slug: org.slug,
            name: org.name,
            kind: "team",
            createdAt: org.createdAt,
          })
          .returning();
        if (!created) {
          throw new Error("insert org returned no row");
        }
        await tx.insert(orgMembers).values({
          orgId: created.id,
          userId: ownerUserId,
          role: "owner",
        });
        return toOrg(created);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "org_slug") {
        throw new OrgSlugTakenError();
      }
      throw error;
    }
  }

  async listOrgMembers(orgId: string): Promise<OrgMemberRecord[]> {
    const rows = await this.db
      .select()
      .from(orgMembers)
      .where(eq(orgMembers.orgId, orgId))
      .orderBy(asc(orgMembers.userId));
    return rows.map(toOrgMember);
  }

  async findOrgMember(orgId: string, userId: string): Promise<OrgMemberRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(orgMembers)
      .where(and(eq(orgMembers.orgId, orgId), eq(orgMembers.userId, userId)))
      .limit(1);
    return row ? toOrgMember(row) : undefined;
  }

  async upsertOrgMember(member: OrgMemberRecord): Promise<OrgMemberRecord> {
    const [row] = await this.db
      .insert(orgMembers)
      .values({
        orgId: member.orgId,
        userId: member.userId,
        role: member.role,
      })
      .onConflictDoUpdate({
        target: [orgMembers.orgId, orgMembers.userId],
        set: { role: member.role },
      })
      .returning();
    if (!row) {
      throw new Error("upsert org member returned no row");
    }
    return toOrgMember(row);
  }

  async createOrgInvite(invite: OrgInviteRecord): Promise<OrgInviteRecord> {
    const [row] = await this.db
      .insert(orgInvites)
      .values({
        id: invite.id,
        orgId: invite.orgId,
        email: invite.email,
        githubLogin: invite.githubLogin,
        role: invite.role,
        expiresAt: invite.expiresAt,
        acceptedAt: invite.acceptedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert org invite returned no row");
    }
    return toOrgInvite(row);
  }

  async findOrgInviteById(id: string): Promise<OrgInviteRecord | undefined> {
    const [row] = await this.db.select().from(orgInvites).where(eq(orgInvites.id, id)).limit(1);
    return row ? toOrgInvite(row) : undefined;
  }

  async acceptOrgInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<OrgInviteRecord | undefined> {
    return this.db.transaction(async (tx) => {
      const [invite] = await tx.select().from(orgInvites).where(eq(orgInvites.id, id)).limit(1);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      const [updated] = await tx
        .update(orgInvites)
        .set({ acceptedAt })
        .where(and(eq(orgInvites.id, id), isNull(orgInvites.acceptedAt)))
        .returning();
      if (!updated) {
        return undefined;
      }
      const [existing] = await tx
        .select()
        .from(orgMembers)
        .where(and(eq(orgMembers.orgId, invite.orgId), eq(orgMembers.userId, userId)))
        .limit(1);
      const role = existing
        ? higherOrgRole(existing.role as OrgRole, invite.role as OrgInviteRole)
        : invite.role;
      await tx
        .insert(orgMembers)
        .values({
          orgId: invite.orgId,
          userId,
          role,
        })
        .onConflictDoUpdate({
          target: [orgMembers.orgId, orgMembers.userId],
          set: { role },
        });
      return toOrgInvite(updated);
    });
  }

  async createProject(project: ProjectRecord, creatorUserId: string): Promise<ProjectRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        const [created] = await tx
          .insert(projects)
          .values({
            id: project.id,
            orgId: project.orgId,
            slug: project.slug,
            name: project.name,
            description: project.description,
            visibility: "private",
            settings: project.settings,
            deletedAt: null,
            createdAt: project.createdAt,
            updatedAt: project.updatedAt,
          })
          .returning();
        if (!created) {
          throw new Error("insert project returned no row");
        }
        await tx.insert(projectMembers).values({
          projectId: created.id,
          userId: creatorUserId,
          role: "admin",
          createdAt: project.createdAt,
        });
        await seedDefaultSecurityConstraints(tx, created.id, project.createdAt);
        return toProject(created);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "project_slug") {
        throw new ProjectSlugTakenError();
      }
      throw error;
    }
  }

  async listProjectsForOrg(orgId: string, userId: string): Promise<ProjectRecord[]> {
    const rows = await this.db
      .select({ project: projects })
      .from(projects)
      .innerJoin(
        projectMembers,
        and(eq(projectMembers.projectId, projects.id), eq(projectMembers.userId, userId)),
      )
      .where(and(eq(projects.orgId, orgId), isNull(projects.deletedAt)))
      .orderBy(asc(projects.createdAt), asc(projects.id));
    return rows.map((row) => toProject(row.project));
  }

  async findProjectById(id: string): Promise<ProjectRecord | undefined> {
    const [row] = await this.db.select().from(projects).where(eq(projects.id, id)).limit(1);
    return row ? toProject(row) : undefined;
  }

  async updateProject(
    id: string,
    patch: { name?: string; description?: string; slug?: string },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined> {
    try {
      const [row] = await this.db
        .update(projects)
        .set({
          ...(patch.name !== undefined ? { name: patch.name } : {}),
          ...(patch.description !== undefined ? { description: patch.description } : {}),
          ...(patch.slug !== undefined ? { slug: patch.slug } : {}),
          updatedAt,
        })
        .where(and(eq(projects.id, id), isNull(projects.deletedAt)))
        .returning();
      return row ? toProject(row) : undefined;
    } catch (error) {
      if (uniqueConstraint(error) === "project_slug") {
        throw new ProjectSlugTakenError();
      }
      throw error;
    }
  }

  async softDeleteProject(id: string, deletedAt: Date): Promise<ProjectRecord | undefined> {
    const [row] = await this.db
      .update(projects)
      .set({ deletedAt, updatedAt: deletedAt })
      .where(and(eq(projects.id, id), isNull(projects.deletedAt)))
      .returning();
    return row ? toProject(row) : undefined;
  }

  async listProjectMembers(projectId: string): Promise<ProjectMemberRecord[]> {
    const rows = await this.db
      .select()
      .from(projectMembers)
      .where(eq(projectMembers.projectId, projectId))
      .orderBy(asc(projectMembers.userId));
    return rows.map(toProjectMember);
  }

  async findProjectMember(
    projectId: string,
    userId: string,
  ): Promise<ProjectMemberRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(projectMembers)
      .where(and(eq(projectMembers.projectId, projectId), eq(projectMembers.userId, userId)))
      .limit(1);
    return row ? toProjectMember(row) : undefined;
  }

  async upsertProjectMember(member: ProjectMemberRecord): Promise<ProjectMemberRecord> {
    const [row] = await this.db
      .insert(projectMembers)
      .values({
        projectId: member.projectId,
        userId: member.userId,
        role: member.role,
        createdAt: member.createdAt,
      })
      .onConflictDoUpdate({
        target: [projectMembers.projectId, projectMembers.userId],
        set: { role: member.role },
      })
      .returning();
    if (!row) {
      throw new Error("upsert project member returned no row");
    }
    return toProjectMember(row);
  }

  async createProjectInvite(invite: ProjectInviteRecord): Promise<ProjectInviteRecord> {
    const [row] = await this.db
      .insert(projectInvites)
      .values({
        id: invite.id,
        projectId: invite.projectId,
        email: invite.email,
        githubLogin: invite.githubLogin,
        role: invite.role,
        invitedBy: invite.invitedBy,
        expiresAt: invite.expiresAt,
        acceptedAt: invite.acceptedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert project invite returned no row");
    }
    return toProjectInvite(row);
  }

  async findProjectInviteById(id: string): Promise<ProjectInviteRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(projectInvites)
      .where(eq(projectInvites.id, id))
      .limit(1);
    return row ? toProjectInvite(row) : undefined;
  }

  async acceptProjectInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<ProjectInviteRecord | undefined> {
    return this.db.transaction(async (tx) => {
      const [invite] = await tx
        .select()
        .from(projectInvites)
        .where(eq(projectInvites.id, id))
        .limit(1);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      const [updated] = await tx
        .update(projectInvites)
        .set({ acceptedAt })
        .where(and(eq(projectInvites.id, id), isNull(projectInvites.acceptedAt)))
        .returning();
      if (!updated) {
        return undefined;
      }
      const [existing] = await tx
        .select()
        .from(projectMembers)
        .where(
          and(eq(projectMembers.projectId, invite.projectId), eq(projectMembers.userId, userId)),
        )
        .limit(1);
      const role = existing
        ? higherProjectRole(existing.role as ProjectRole, invite.role as ProjectRole)
        : invite.role;
      await tx
        .insert(projectMembers)
        .values({
          projectId: invite.projectId,
          userId,
          role,
          createdAt: existing?.createdAt ?? acceptedAt,
        })
        .onConflictDoUpdate({
          target: [projectMembers.projectId, projectMembers.userId],
          set: { role },
        });
      return toProjectInvite(updated);
    });
  }

  async createMilestone(milestone: MilestoneRecord): Promise<MilestoneRecord> {
    const [row] = await this.db
      .insert(milestones)
      .values({
        id: milestone.id,
        projectId: milestone.projectId,
        title: milestone.title,
        description: milestone.description,
        status: milestone.status,
        targetDate: milestone.targetDate,
        sortOrder: milestone.sortOrder,
        createdAt: milestone.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert milestone returned no row");
    }
    return toMilestone(row);
  }

  async listMilestones(projectId: string): Promise<MilestoneRecord[]> {
    const rows = await this.db
      .select()
      .from(milestones)
      .where(eq(milestones.projectId, projectId))
      .orderBy(asc(milestones.sortOrder), asc(milestones.createdAt), asc(milestones.id));
    return rows.map(toMilestone);
  }

  async findMilestoneById(id: string): Promise<MilestoneRecord | undefined> {
    const [row] = await this.db.select().from(milestones).where(eq(milestones.id, id)).limit(1);
    return row ? toMilestone(row) : undefined;
  }

  async createTask(task: TaskRecord): Promise<TaskRecord> {
    const [row] = await this.db
      .insert(tasks)
      .values({
        id: task.id,
        projectId: task.projectId,
        milestoneId: task.milestoneId,
        parentId: task.parentId,
        title: task.title,
        description: task.description,
        status: task.status,
        priority: task.priority,
        type: task.type,
        version: task.version,
        assigneeUserId: task.assigneeUserId,
        assigneeAgentName: task.assigneeAgentName,
        agentBrief: task.agentBrief,
        linkedPaths: task.linkedPaths,
        githubIssueId: task.githubIssueId,
        lockedBySessionId: task.lockedBySessionId,
        lockExpiresAt: task.lockExpiresAt,
        deletedAt: task.deletedAt,
        createdAt: task.createdAt,
        updatedAt: task.updatedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert task returned no row");
    }
    return toTask(row);
  }

  async listTasks(projectId: string): Promise<TaskRecord[]> {
    const rows = await this.db
      .select()
      .from(tasks)
      .where(and(eq(tasks.projectId, projectId), isNull(tasks.deletedAt)));
    return rows.map(toTask);
  }

  async findTaskById(id: string): Promise<TaskRecord | undefined> {
    const [row] = await this.db.select().from(tasks).where(eq(tasks.id, id)).limit(1);
    return row ? toTask(row) : undefined;
  }

  async updateTask(
    id: string,
    expectedVersion: number,
    patch: TaskPatch,
    updatedAt: Date,
    options?: { releaseLock?: boolean },
  ): Promise<{ task: TaskRecord; lockReleased: boolean } | undefined> {
    return this.db.transaction(async (tx) => {
      const [current] = await tx.select().from(tasks).where(eq(tasks.id, id)).limit(1);
      if (!current || current.deletedAt) {
        return undefined;
      }
      if (current.version !== expectedVersion) {
        throw new VersionConflictError(toTask(current));
      }
      const lockReleased = Boolean(options?.releaseLock && current.lockedBySessionId);
      const [row] = await tx
        .update(tasks)
        .set({
          ...(patch.title !== undefined ? { title: patch.title } : {}),
          ...(patch.description !== undefined ? { description: patch.description } : {}),
          ...(patch.status !== undefined ? { status: patch.status } : {}),
          ...(patch.type !== undefined ? { type: patch.type } : {}),
          ...(patch.priority !== undefined ? { priority: patch.priority } : {}),
          ...(patch.milestoneId !== undefined ? { milestoneId: patch.milestoneId } : {}),
          ...(patch.parentId !== undefined ? { parentId: patch.parentId } : {}),
          ...(patch.assigneeUserId !== undefined ? { assigneeUserId: patch.assigneeUserId } : {}),
          ...(patch.assigneeAgentName !== undefined
            ? { assigneeAgentName: patch.assigneeAgentName }
            : {}),
          ...(patch.agentBrief !== undefined ? { agentBrief: patch.agentBrief } : {}),
          ...(patch.linkedPaths !== undefined ? { linkedPaths: patch.linkedPaths } : {}),
          ...(options?.releaseLock ? { lockedBySessionId: null, lockExpiresAt: null } : {}),
          version: current.version + 1,
          updatedAt,
        })
        .where(and(eq(tasks.id, id), eq(tasks.version, expectedVersion), isNull(tasks.deletedAt)))
        .returning();
      if (!row) {
        const [fresh] = await tx.select().from(tasks).where(eq(tasks.id, id)).limit(1);
        if (fresh && !fresh.deletedAt) {
          throw new VersionConflictError(toTask(fresh));
        }
        return undefined;
      }
      return { task: toTask(row), lockReleased };
    });
  }

  async softDeleteTask(id: string, deletedAt: Date): Promise<TaskRecord | undefined> {
    const [current] = await this.db.select().from(tasks).where(eq(tasks.id, id)).limit(1);
    if (!current || current.deletedAt) {
      return undefined;
    }
    const [row] = await this.db
      .update(tasks)
      .set({
        deletedAt,
        updatedAt: deletedAt,
        version: current.version + 1,
      })
      .where(and(eq(tasks.id, id), isNull(tasks.deletedAt)))
      .returning();
    return row ? toTask(row) : undefined;
  }

  async createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord> {
    const [row] = await this.db
      .insert(taskComments)
      .values({
        id: comment.id,
        taskId: comment.taskId,
        authorType: comment.authorType,
        authorId: comment.authorId,
        body: comment.body,
        createdAt: comment.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert comment returned no row");
    }
    return toComment(row);
  }

  async addDependency(dependency: TaskDependencyRecord): Promise<TaskDependencyRecord> {
    if (dependency.fromTaskId === dependency.toTaskId) {
      throw new DependencyCycleError();
    }
    return this.db.transaction(async (tx) => {
      const [fromTask] = await tx
        .select({ projectId: tasks.projectId })
        .from(tasks)
        .where(eq(tasks.id, dependency.fromTaskId))
        .limit(1);
      if (!fromTask) {
        throw new Error("task not found");
      }
      // Serialize writers for this project so opposite blocks edges cannot both commit.
      await tx.execute(
        sql`select pg_advisory_xact_lock(${DEPENDENCY_LOCK_NS}, hashtext(${fromTask.projectId}))`,
      );
      const endpointIds = [dependency.fromTaskId, dependency.toTaskId].sort();
      await tx
        .select({ id: tasks.id })
        .from(tasks)
        .where(inArray(tasks.id, endpointIds))
        .orderBy(asc(tasks.id))
        .for("update");

      const [existing] = await tx
        .select()
        .from(taskDependencies)
        .where(
          and(
            eq(taskDependencies.fromTaskId, dependency.fromTaskId),
            eq(taskDependencies.toTaskId, dependency.toTaskId),
            eq(taskDependencies.type, dependency.type),
          ),
        )
        .limit(1);
      if (existing) {
        return toDependency(existing);
      }
      if (dependency.type === "blocks") {
        const edges = await tx
          .select({
            fromTaskId: taskDependencies.fromTaskId,
            toTaskId: taskDependencies.toTaskId,
            type: taskDependencies.type,
          })
          .from(taskDependencies)
          .innerJoin(tasks, eq(tasks.id, taskDependencies.fromTaskId))
          .where(and(eq(taskDependencies.type, "blocks"), eq(tasks.projectId, fromTask.projectId)));
        if (wouldCreateCycle(edges, dependency.fromTaskId, dependency.toTaskId)) {
          throw new DependencyCycleError();
        }
      }
      try {
        const [row] = await tx
          .insert(taskDependencies)
          .values({
            fromTaskId: dependency.fromTaskId,
            toTaskId: dependency.toTaskId,
            type: dependency.type,
          })
          .returning();
        if (!row) {
          throw new Error("insert dependency returned no row");
        }
        return toDependency(row);
      } catch (error) {
        if (uniqueConstraint(error)) {
          const [row] = await tx
            .select()
            .from(taskDependencies)
            .where(
              and(
                eq(taskDependencies.fromTaskId, dependency.fromTaskId),
                eq(taskDependencies.toTaskId, dependency.toTaskId),
                eq(taskDependencies.type, dependency.type),
              ),
            )
            .limit(1);
          if (row) {
            return toDependency(row);
          }
        }
        throw error;
      }
    });
  }

  async writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord> {
    const [row] = await this.db
      .insert(activityEvents)
      .values({
        id: event.id,
        projectId: event.projectId,
        objectType: event.objectType,
        objectId: event.objectId,
        actorType: event.actorType,
        actorId: event.actorId,
        verb: event.verb,
        payload: event.payload,
        createdAt: event.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert activity returned no row");
    }
    return toActivity(row);
  }

  async listActivity(
    projectId: string,
    filters?: { objectType?: string; objectId?: string },
  ): Promise<ActivityEventRecord[]> {
    const rows = await this.db
      .select()
      .from(activityEvents)
      .where(
        and(
          eq(activityEvents.projectId, projectId),
          filters?.objectType ? eq(activityEvents.objectType, filters.objectType) : undefined,
          filters?.objectId ? eq(activityEvents.objectId, filters.objectId) : undefined,
        ),
      )
      .orderBy(desc(activityEvents.createdAt), desc(activityEvents.id));
    return rows.map(toActivity);
  }

  async listContextNodes(projectId: string): Promise<ContextNodeRecord[]> {
    const rows = await this.db
      .select()
      .from(contextNodes)
      .where(eq(contextNodes.projectId, projectId))
      .orderBy(asc(contextNodes.id));
    return rows.flatMap((row) => {
      const node = toContextNode(row);
      return node ? [node] : [];
    });
  }

  async upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    const existing = await this.db
      .select()
      .from(contextNodes)
      .where(
        and(
          eq(contextNodes.projectId, node.projectId),
          eq(contextNodes.scopeType, node.scopeType),
          eq(contextNodes.path, node.path),
          node.repoId === null ? isNull(contextNodes.repoId) : eq(contextNodes.repoId, node.repoId),
          node.taskId === null ? isNull(contextNodes.taskId) : eq(contextNodes.taskId, node.taskId),
        ),
      )
      .limit(1);
    const current = existing[0];
    if (current) {
      const [row] = await this.db
        .update(contextNodes)
        .set({
          sections: node.sections,
          sectionsText: node.sectionsText,
          source: node.source,
          sourcePath: node.sourcePath,
          reviewState: node.reviewState,
          updatedByType: node.updatedByType,
          updatedById: node.updatedById,
          updatedAt: node.updatedAt,
        })
        .where(eq(contextNodes.id, current.id))
        .returning();
      if (!row) {
        throw new Error("update context node returned no row");
      }
      const stored = toContextNode(row);
      if (!stored) {
        throw new Error("update context node returned invalid row");
      }
      return stored;
    }
    const [row] = await this.db
      .insert(contextNodes)
      .values({
        id: node.id,
        projectId: node.projectId,
        repoId: node.repoId,
        taskId: node.taskId,
        scopeType: node.scopeType,
        path: node.path,
        sections: node.sections,
        sectionsText: node.sectionsText,
        source: node.source,
        sourcePath: node.sourcePath,
        reviewState: node.reviewState,
        updatedByType: node.updatedByType,
        updatedById: node.updatedById,
        updatedAt: node.updatedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert context node returned no row");
    }
    const stored = toContextNode(row);
    if (!stored) {
      throw new Error("insert context node returned invalid row");
    }
    return stored;
  }

  async listActiveConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const rows = await this.db
      .select()
      .from(constraints)
      .where(and(eq(constraints.projectId, projectId), eq(constraints.status, "active")))
      .orderBy(asc(constraints.id));
    return rows.flatMap((row) => {
      const constraint = toConstraint(row);
      return constraint ? [constraint] : [];
    });
  }

  async listConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const rows = await this.db
      .select()
      .from(constraints)
      .where(eq(constraints.projectId, projectId))
      .orderBy(asc(constraints.id));
    return rows.flatMap((row) => {
      const constraint = toConstraint(row);
      return constraint ? [constraint] : [];
    });
  }

  async insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    const [row] = await this.db
      .insert(constraints)
      .values({
        id: constraint.id,
        projectId: constraint.projectId,
        kind: constraint.kind,
        body: constraint.body,
        scopePath: constraint.scopePath,
        status: constraint.status,
        createdAt: constraint.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert constraint returned no row");
    }
    const stored = toConstraint(row);
    if (!stored) {
      throw new Error("insert constraint returned invalid row");
    }
    return stored;
  }

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
    return this.db.transaction(async (tx) => {
      await tx
        .delete(codeOwners)
        .where(and(eq(codeOwners.repoId, repoId), eq(codeOwners.source, "codeowners")));
      const byPattern = new Map<string, CodeOwnerRecord>();
      for (const row of rows) {
        byPattern.set(row.pathPattern, row);
      }
      const values = [...byPattern.values()].map((row) => ({
        id: row.id,
        repoId,
        pathPattern: row.pathPattern,
        owners: row.owners,
        source: row.source,
      }));
      if (values.length === 0) {
        return [];
      }
      const inserted = await tx.insert(codeOwners).values(values).returning();
      return inserted
        .map(toCodeOwner)
        .sort((a, b) => a.pathPattern.localeCompare(b.pathPattern) || a.id.localeCompare(b.id));
    });
  }

  async listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]> {
    const rows = await this.db
      .select()
      .from(decisions)
      .where(and(eq(decisions.projectId, projectId), eq(decisions.status, "accepted")))
      .orderBy(asc(decisions.id));
    if (rows.length === 0) {
      return [];
    }
    const paths = await this.db
      .select()
      .from(decisionPaths)
      .where(
        inArray(
          decisionPaths.decisionId,
          rows.map((row) => row.id),
        ),
      );
    const byDecision = new Map<string, string[]>();
    for (const path of paths) {
      const list = byDecision.get(path.decisionId) ?? [];
      list.push(path.path);
      byDecision.set(path.decisionId, list);
    }
    return rows.flatMap((row) => {
      const decision = toDecision(row, byDecision.get(row.id) ?? []);
      return decision ? [decision] : [];
    });
  }

  async insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord> {
    const [row] = await this.db
      .insert(contextRevisions)
      .values({
        id: revision.id,
        projectId: revision.projectId,
        compiledHash: revision.compiledHash,
        compilerVersion: revision.compilerVersion,
        target: revision.target,
        briefMarkdown: revision.briefMarkdown,
        briefJson: revision.briefJson,
        tokenEstimate: revision.tokenEstimate,
        sourceNodeIds: revision.sourceNodeIds,
        sessionId: revision.sessionId,
        createdAt: revision.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert context revision returned no row");
    }
    return toContextRevision(row);
  }

  async listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]> {
    const rows = await this.db
      .select()
      .from(contextRevisions)
      .where(eq(contextRevisions.projectId, projectId))
      .orderBy(desc(contextRevisions.createdAt), desc(contextRevisions.id));
    return rows.map(toContextRevision);
  }

  async withIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: (writes: IdempotentWrites) => Promise<unknown>,
  ): Promise<unknown> {
    return this.db.transaction(async (tx) => {
      await tx.execute(
        sql`select pg_advisory_xact_lock(${IDEMPOTENCY_LOCK_NS}, hashtext(${`${actorType}:${actorId}:${key}`}))`,
      );
      const keyMatch = and(
        eq(idempotencyKeys.actorType, actorType),
        eq(idempotencyKeys.actorId, actorId),
        eq(idempotencyKeys.key, key),
      );
      const [existing] = await tx.select().from(idempotencyKeys).where(keyMatch).limit(1);
      if (existing && now.getTime() - existing.createdAt.getTime() <= IDEMPOTENCY_TTL_MS) {
        return existing.response;
      }
      if (existing) {
        await tx.delete(idempotencyKeys).where(keyMatch);
      }

      const writes: IdempotentWrites = {
        createTask: async (task) => {
          const [row] = await tx
            .insert(tasks)
            .values({
              id: task.id,
              projectId: task.projectId,
              milestoneId: task.milestoneId,
              parentId: task.parentId,
              title: task.title,
              description: task.description,
              status: task.status,
              priority: task.priority,
              type: task.type,
              version: task.version,
              assigneeUserId: task.assigneeUserId,
              assigneeAgentName: task.assigneeAgentName,
              agentBrief: task.agentBrief,
              linkedPaths: task.linkedPaths,
              githubIssueId: task.githubIssueId,
              lockedBySessionId: task.lockedBySessionId,
              lockExpiresAt: task.lockExpiresAt,
              deletedAt: task.deletedAt,
              createdAt: task.createdAt,
              updatedAt: task.updatedAt,
            })
            .returning();
          if (!row) {
            throw new Error("insert task returned no row");
          }
          return toTask(row);
        },
        createComment: async (comment) => {
          const [row] = await tx
            .insert(taskComments)
            .values({
              id: comment.id,
              taskId: comment.taskId,
              authorType: comment.authorType,
              authorId: comment.authorId,
              body: comment.body,
              createdAt: comment.createdAt,
            })
            .returning();
          if (!row) {
            throw new Error("insert comment returned no row");
          }
          return toComment(row);
        },
        writeActivity: async (event) => {
          const [row] = await tx
            .insert(activityEvents)
            .values({
              id: event.id,
              projectId: event.projectId,
              objectType: event.objectType,
              objectId: event.objectId,
              actorType: event.actorType,
              actorId: event.actorId,
              verb: event.verb,
              payload: event.payload,
              createdAt: event.createdAt,
            })
            .returning();
          if (!row) {
            throw new Error("insert activity returned no row");
          }
          return toActivity(row);
        },
      };

      const response = await produce(writes);
      await tx.insert(idempotencyKeys).values({
        actorType,
        actorId,
        key,
        response,
        createdAt: now,
      });
      return response;
    });
  }
  async createApiToken(token: TokenRecord): Promise<TokenRecord> {
    const [row] = await this.db
      .insert(apiTokens)
      .values({
        id: token.id,
        projectId: token.projectId,
        name: token.name,
        tokenHash: token.tokenHash,
        prefix: token.prefix,
        scopes: token.scopes,
        createdBy: token.createdBy,
        lastUsedAt: token.lastUsedAt,
        expiresAt: token.expiresAt,
        revokedAt: token.revokedAt,
        createdAt: token.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert api token returned no row");
    }
    return toToken(row);
  }

  async listApiTokens(projectId: string): Promise<TokenRecord[]> {
    const rows = await this.db
      .select()
      .from(apiTokens)
      .where(eq(apiTokens.projectId, projectId))
      .orderBy(asc(apiTokens.createdAt), asc(apiTokens.id));
    return rows.map(toToken);
  }

  async findApiTokenById(id: string): Promise<TokenRecord | undefined> {
    const [row] = await this.db.select().from(apiTokens).where(eq(apiTokens.id, id)).limit(1);
    return row ? toToken(row) : undefined;
  }

  async findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(apiTokens)
      .where(eq(apiTokens.tokenHash, tokenHash))
      .limit(1);
    return row ? toToken(row) : undefined;
  }

  async touchApiToken(id: string, lastUsedAt: Date): Promise<void> {
    await this.db.update(apiTokens).set({ lastUsedAt }).where(eq(apiTokens.id, id));
  }

  async revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined> {
    const [row] = await this.db
      .update(apiTokens)
      .set({ revokedAt })
      .where(and(eq(apiTokens.id, id), isNull(apiTokens.revokedAt)))
      .returning();
    if (row) {
      return toToken(row);
    }
    const existing = await this.findApiTokenById(id);
    return existing;
  }

  async createApproval(approval: ApprovalRecord): Promise<ApprovalRecord> {
    const [row] = await this.db
      .insert(approvalRequests)
      .values({
        id: approval.id,
        projectId: approval.projectId,
        sessionId: approval.sessionId,
        action: approval.action,
        payload: approval.payload,
        status: approval.status,
        requestedAt: approval.requestedAt,
        resolvedAt: approval.resolvedAt,
        resolvedBy: approval.resolvedBy,
      })
      .returning();
    if (!row) {
      throw new Error("insert approval returned no row");
    }
    return toApproval(row);
  }

  async listApprovals(
    projectId: string,
    status?: ApprovalRecord["status"],
  ): Promise<ApprovalRecord[]> {
    const rows = await this.db
      .select()
      .from(approvalRequests)
      .where(
        status
          ? and(eq(approvalRequests.projectId, projectId), eq(approvalRequests.status, status))
          : eq(approvalRequests.projectId, projectId),
      )
      .orderBy(asc(approvalRequests.requestedAt), asc(approvalRequests.id));
    return rows.map(toApproval);
  }

  async findApprovalById(id: string): Promise<ApprovalRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(approvalRequests)
      .where(eq(approvalRequests.id, id))
      .limit(1);
    return row ? toApproval(row) : undefined;
  }

  async resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined> {
    const [row] = await this.db
      .update(approvalRequests)
      .set({
        status: decision,
        resolvedAt,
        resolvedBy,
      })
      .where(and(eq(approvalRequests.id, id), eq(approvalRequests.status, "pending")))
      .returning();
    return row ? toApproval(row) : undefined;
  }

  async consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord> {
    const [row] = await this.db
      .insert(rateBuckets)
      .values({
        bucketKey: input.bucketKey,
        windowStart: input.windowStart,
        count: input.countDelta,
        bytes: BigInt(input.bytesDelta),
      })
      .onConflictDoUpdate({
        target: rateBuckets.bucketKey,
        set: {
          windowStart: sql`case when ${rateBuckets.windowStart} = ${input.windowStart} then ${rateBuckets.windowStart} else ${input.windowStart} end`,
          count: sql`case when ${rateBuckets.windowStart} = ${input.windowStart} then ${rateBuckets.count} + ${input.countDelta} else ${input.countDelta} end`,
          bytes: sql`case when ${rateBuckets.windowStart} = ${input.windowStart} then ${rateBuckets.bytes} + ${input.bytesDelta} else ${input.bytesDelta} end`,
        },
      })
      .returning();
    if (!row) {
      throw new Error("upsert rate bucket returned no row");
    }
    return toRateBucket(row);
  }

  async findAgentSessionById(id: string): Promise<AgentSessionRef | undefined> {
    const [row] = await this.db
      .select({ id: agentSessions.id, projectId: agentSessions.projectId })
      .from(agentSessions)
      .where(eq(agentSessions.id, id))
      .limit(1);
    return row ?? undefined;
  }
}
