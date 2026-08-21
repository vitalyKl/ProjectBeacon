import {
  DEFAULT_PROJECT_LABELS,
  DEFAULT_PROJECT_LABEL_STATUS,
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
  handoffs,
  labels,
  milestones,
  orgInvites,
  orgMembers,
  orgs,
  projectInvites,
  projectMembers,
  projectRepos,
  projectReports,
  projectReviews,
  projects,
  rateBuckets,
  taskComments,
  taskDependencies,
  tasks,
  userSessions,
  users,
  type Db,
  decisionTasks,
  githubInstallations,
  githubSyncState,
  sidecarConnections,
} from "@beacon/db";
import { isScope, uuidv7, type Scope } from "@beacon/shared";
import { and, eq, isNull, sql } from "drizzle-orm";
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
  type DecisionPathLink,
} from "../../context/types.js";
import { GithubIdTakenError, LoginTakenError } from "../identity.js";
import type {
  OrgInviteRecord,
  OrgInviteRole,
  OrgKind,
  OrgMemberRecord,
  OrgRecord,
  OrgRole,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
  ProjectRole,
} from "../../orgs/types.js";
import type { LabelRecord, LabelStatus } from "../../labels/types.js";
import { isLabelStatus } from "../../labels/types.js";
import type { ApprovalStatus } from "../../tokens/types.js";
import type {
  ActivityEventRecord,
  CommentAuthorType,
  DependencyType,
  LinkedPath,
  MilestoneRecord,
  MilestoneStatus,
  TaskCommentRecord,
  TaskDependencyRecord,
  TaskRecord,
  TaskStatus,
  TaskType,
} from "../../roadmap/types.js";
import type { ApprovalRecord, RateBucketRecord, TokenRecord } from "../../tokens/types.js";
import type { SessionRecord, UserRecord } from "../identity.js";
import {
  InvalidReferenceError,
  isAgentHost,
  isAgentSessionStatus,
  isLockActive,
  TaskLockedError,
  type AgentSessionRecord,
  type HandoffRecord,
  type StartWorkInput,
  type StartWorkWriteResult,
} from "../../sessions/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../../github/types.js";
import type { ProjectReportRecord, ProjectReviewRecord, ReportSnapshot } from "../../reports/types.js";
import { isReviewStatus } from "../../reports/types.js";

export const BOOTSTRAP_LOCK_KEY = 8_811_201;
export const IDEMPOTENCY_LOCK_NS = 8_811_202;
export const DEPENDENCY_LOCK_NS = 8_811_203;

export type UniqueConstraint =
  | "login"
  | "github_id"
  | "org_slug"
  | "project_slug"
  | "context_node_scope"
  | "project_repo"
  | "label_slug"
  | "unknown";

export type Tx = Db;

export function uniqueIds(ids: string[]): string[] {
  const unique: string[] = [];
  const seen = new Set<string>();
  for (const id of ids) {
    if (seen.has(id)) {
      continue;
    }
    seen.add(id);
    unique.push(id);
  }
  return unique;
}

export function uniqueConstraint(error: unknown): UniqueConstraint | undefined {
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
        if (constraint.includes("context_nodes_unique_scope")) {
          return "context_node_scope";
        }
        if (constraint.includes("project_repos")) {
          return "project_repo";
        }
        if (constraint.includes("labels_project_slug")) {
          return "label_slug";
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

export function mapUserInsertError(error: unknown): never {
  const constraint = uniqueConstraint(error);
  if (constraint === "login") {
    throw new LoginTakenError();
  }
  if (constraint === "github_id") {
    throw new GithubIdTakenError();
  }
  throw error;
}

export function asBuffer(value: Buffer | Uint8Array): Buffer {
  return Buffer.isBuffer(value) ? value : Buffer.from(value);
}

export function toUser(row: typeof users.$inferSelect): UserRecord {
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

export function toOrg(row: typeof orgs.$inferSelect): OrgRecord {
  return {
    id: row.id,
    slug: row.slug,
    name: row.name,
    kind: row.kind as OrgKind,
    createdAt: row.createdAt,
  };
}

export function toOrgMember(row: typeof orgMembers.$inferSelect): OrgMemberRecord {
  return {
    orgId: row.orgId,
    userId: row.userId,
    role: row.role as OrgRole,
  };
}

export function toOrgInvite(row: typeof orgInvites.$inferSelect): OrgInviteRecord {
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

export function asSettings(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function toProject(row: typeof projects.$inferSelect): ProjectRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    slug: row.slug,
    name: row.name,
    description: row.description,
    visibility: "private",
    defaultRepoId: row.defaultRepoId,
    settings: asSettings(row.settings),
    deletedAt: row.deletedAt,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

export function toProjectMember(row: typeof projectMembers.$inferSelect): ProjectMemberRecord {
  return {
    projectId: row.projectId,
    userId: row.userId,
    role: row.role as ProjectRole,
    createdAt: row.createdAt,
  };
}

export function toProjectInvite(row: typeof projectInvites.$inferSelect): ProjectInviteRecord {
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

export function asLinkedPaths(value: unknown): LinkedPath[] {
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

export function asPayload(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function toMilestone(row: typeof milestones.$inferSelect): MilestoneRecord {
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

export function toTask(row: typeof tasks.$inferSelect): TaskRecord {
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
    howToCheck: row.howToCheck,
    linkedPaths: asLinkedPaths(row.linkedPaths),
    githubIssueId: row.githubIssueId,
    lockedBySessionId: row.lockedBySessionId,
    lockExpiresAt: row.lockExpiresAt,
    deletedAt: row.deletedAt,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

export function toComment(row: typeof taskComments.$inferSelect): TaskCommentRecord {
  return {
    id: row.id,
    taskId: row.taskId,
    authorType: row.authorType as CommentAuthorType,
    authorId: row.authorId,
    body: row.body,
    createdAt: row.createdAt,
  };
}

export function toDependency(row: typeof taskDependencies.$inferSelect): TaskDependencyRecord {
  return {
    fromTaskId: row.fromTaskId,
    toTaskId: row.toTaskId,
    type: row.type as DependencyType,
  };
}

export function toActivity(row: typeof activityEvents.$inferSelect): ActivityEventRecord {
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

export function asSections(value: unknown): ContextSection[] {
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
      id === "style" ||
      id === "definition_of_done"
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

export function toContextNode(row: typeof contextNodes.$inferSelect): ContextNodeRecord | undefined {
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

export function toProjectRepo(row: typeof projectRepos.$inferSelect): ProjectRepoRecord | undefined {
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

export function toCodeOwner(row: typeof codeOwners.$inferSelect): CodeOwnerRecord {
  return {
    id: row.id,
    repoId: row.repoId,
    pathPattern: row.pathPattern,
    owners: [...row.owners],
    source: row.source,
  };
}

export async function upsertContextNodeInDb(
  db: Db,
  node: ContextNodeRecord,
): Promise<ContextNodeRecord> {
  const scopeMatch = and(
    eq(contextNodes.projectId, node.projectId),
    eq(contextNodes.scopeType, node.scopeType),
    eq(contextNodes.path, node.path),
    node.repoId === null ? isNull(contextNodes.repoId) : eq(contextNodes.repoId, node.repoId),
    node.taskId === null ? isNull(contextNodes.taskId) : eq(contextNodes.taskId, node.taskId),
  );
  await db.execute(sql`
    INSERT INTO "context_nodes" (
      "id", "project_id", "repo_id", "task_id", "scope_type", "path",
      "sections", "sections_text", "source", "source_path", "review_state",
      "updated_by_type", "updated_by_id", "updated_at"
    ) VALUES (
      ${node.id},
      ${node.projectId},
      ${node.repoId},
      ${node.taskId},
      ${node.scopeType},
      ${node.path},
      ${JSON.stringify(node.sections)}::jsonb,
      ${node.sectionsText},
      ${node.source},
      ${node.sourcePath},
      ${node.reviewState},
      ${node.updatedByType},
      ${node.updatedById},
      ${node.updatedAt}
    )
    ON CONFLICT (
      "project_id",
      "scope_type",
      (COALESCE("repo_id", '00000000-0000-0000-0000-000000000000')),
      "path",
      (COALESCE("task_id", '00000000-0000-0000-0000-000000000000'))
    )
    DO UPDATE SET
      "sections" = EXCLUDED."sections",
      "sections_text" = EXCLUDED."sections_text",
      "source" = EXCLUDED."source",
      "source_path" = EXCLUDED."source_path",
      "review_state" = EXCLUDED."review_state",
      "updated_by_type" = EXCLUDED."updated_by_type",
      "updated_by_id" = EXCLUDED."updated_by_id",
      "updated_at" = EXCLUDED."updated_at"
  `);
  const [row] = await db.select().from(contextNodes).where(scopeMatch).limit(1);
  if (!row) {
    throw new Error("upsert context node returned no row");
  }
  const stored = toContextNode(row);
  if (!stored) {
    throw new Error("upsert context node returned invalid row");
  }
  return stored;
}

export async function upsertCodeOwnersInTx(
  tx: Db,
  repoId: string,
  rows: CodeOwnerRecord[],
): Promise<CodeOwnerRecord[]> {
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
}

export async function seedDefaultSecurityConstraints(
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

export async function seedDefaultProjectLabels(
  tx: Pick<Db, "select" | "insert">,
  projectId: string,
  createdAt: Date,
): Promise<void> {
  const existing = await tx
    .select({ slug: labels.slug })
    .from(labels)
    .where(eq(labels.projectId, projectId));
  const existingSlugs = new Set(existing.map((row) => row.slug));
  const values = DEFAULT_PROJECT_LABELS.flatMap((seed, index) => {
    if (existingSlugs.has(seed.slug)) {
      return [];
    }
    return [
      {
        id: uuidv7(createdAt.getTime() + index),
        projectId,
        slug: seed.slug,
        name: seed.name,
        description: seed.description,
        color: seed.color,
        status: DEFAULT_PROJECT_LABEL_STATUS,
        createdAt,
      },
    ];
  });
  if (values.length === 0) {
    return;
  }
  await tx.insert(labels).values(values);
}

export function toConstraint(row: typeof constraints.$inferSelect): ConstraintRecord | undefined {
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

export function toLabel(row: typeof labels.$inferSelect, paths: LinkedPath[]): LabelRecord {
  const status: LabelStatus = isLabelStatus(row.status) ? row.status : "active";
  return {
    id: row.id,
    projectId: row.projectId,
    slug: row.slug,
    name: row.name,
    description: row.description,
    color: row.color,
    status,
    createdAt: row.createdAt,
    paths,
  };
}

export function toDecision(
  row: typeof decisions.$inferSelect,
  relatedPaths: DecisionPathLink[],
  relatedTaskIds: string[],
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
    relatedTaskIds,
  };
}

export function asRevisionTarget(value: unknown): ContextRevisionTarget {
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

export function asBriefJson(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function toAgentSession(row: typeof agentSessions.$inferSelect): AgentSessionRecord | undefined {
  if (!isAgentSessionStatus(row.status) || !isAgentHost(row.agentHost)) {
    return undefined;
  }
  return {
    id: row.id,
    projectId: row.projectId,
    taskId: row.taskId,
    tokenId: row.tokenId,
    agentName: row.agentName,
    agentHost: row.agentHost,
    status: row.status,
    contextRevisionId: row.contextRevisionId,
    startedAt: row.startedAt,
    finishedAt: row.finishedAt,
    lockExpiresAt: row.lockExpiresAt,
    lastHeartbeatAt: row.lastHeartbeatAt,
  };
}

export function toHandoff(row: typeof handoffs.$inferSelect): HandoffRecord {
  return {
    id: row.id,
    sessionId: row.sessionId,
    taskId: row.taskId,
    summary: row.summary,
    nextSteps: row.nextSteps,
    filesTouched: asLinkedPaths(row.filesTouched),
    openQuestions: row.openQuestions ?? [],
    createdAt: row.createdAt,
  };
}

export function toContextRevision(row: typeof contextRevisions.$inferSelect): ContextRevisionRecord {
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

export function toSession(row: typeof userSessions.$inferSelect): SessionRecord {
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

export function asScopes(value: string[] | null): Scope[] {
  if (!value) {
    return [];
  }
  return value.filter(isScope);
}

export function toToken(row: typeof apiTokens.$inferSelect): TokenRecord {
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

export function toApproval(row: typeof approvalRequests.$inferSelect): ApprovalRecord {
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

export function toRateBucket(row: typeof rateBuckets.$inferSelect): RateBucketRecord {
  return {
    bucketKey: row.bucketKey,
    windowStart: row.windowStart,
    count: row.count,
    bytes: row.bytes,
  };
}


export function toReport(row: typeof projectReports.$inferSelect): ProjectReportRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    title: row.title,
    bodyMd: row.bodyMd,
    snapshot: asReportSnapshot(row.snapshot),
    createdByType: row.createdByType,
    createdById: row.createdById,
    createdAt: row.createdAt,
  };
}

export function toReview(row: typeof projectReviews.$inferSelect): ProjectReviewRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    title: row.title,
    bodyMd: row.bodyMd,
    source: row.source,
    sourcePath: row.sourcePath,
    status: isReviewStatus(row.status) ? row.status : "needs_review",
    createdByType: row.createdByType,
    createdById: row.createdById,
    createdAt: row.createdAt,
  };
}

export function asReportSnapshot(value: unknown): ReportSnapshot {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return {
      generated_at: new Date(0).toISOString(),
      milestones: { open: 0, closed: 0 },
      tasks: {},
      ready_task_ids: [],
      in_flight_task_ids: [],
      review_ids: [],
    };
  }
  const record = value as Record<string, unknown>;
  const milestones =
    record["milestones"] && typeof record["milestones"] === "object" && !Array.isArray(record["milestones"])
      ? (record["milestones"] as Record<string, unknown>)
      : {};
  const tasks =
    record["tasks"] && typeof record["tasks"] === "object" && !Array.isArray(record["tasks"])
      ? (record["tasks"] as Record<string, unknown>)
      : {};
  const counts: Record<string, number> = {};
  for (const [key, count] of Object.entries(tasks)) {
    if (typeof count === "number") {
      counts[key] = count;
    }
  }
  return {
    generated_at: typeof record["generated_at"] === "string" ? record["generated_at"] : new Date(0).toISOString(),
    milestones: {
      open: typeof milestones["open"] === "number" ? milestones["open"] : 0,
      closed: typeof milestones["closed"] === "number" ? milestones["closed"] : 0,
    },
    tasks: counts,
    ready_task_ids: stringArray(record["ready_task_ids"]),
    in_flight_task_ids: stringArray(record["in_flight_task_ids"]),
    review_ids: stringArray(record["review_ids"]),
  };
}

export function stringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter((item): item is string => typeof item === "string");
}

export async function startWorkInTx(tx: Tx, input: StartWorkInput): Promise<StartWorkWriteResult> {
  if (input.session.tokenId) {
    const [token] = await tx
      .select({ id: apiTokens.id, projectId: apiTokens.projectId })
      .from(apiTokens)
      .where(eq(apiTokens.id, input.session.tokenId))
      .limit(1);
    if (!token || token.projectId !== input.session.projectId) {
      throw new InvalidReferenceError("token");
    }
  }

  let stolenFrom: string | null = null;
  if (input.session.taskId) {
    const [task] = await tx
      .select()
      .from(tasks)
      .where(eq(tasks.id, input.session.taskId))
      .limit(1)
      .for("update");
    if (!task || task.deletedAt || task.projectId !== input.session.projectId) {
      throw new InvalidReferenceError("task");
    }
    const mapped = toTask(task);
    if (task.lockedBySessionId && task.lockedBySessionId !== input.session.id) {
      if (isLockActive(mapped, input.now) && !input.steal) {
        throw new TaskLockedError(mapped);
      }
      stolenFrom = task.lockedBySessionId;
    }
  }

  await tx.insert(contextRevisions).values({
    id: input.revision.id,
    projectId: input.revision.projectId,
    compiledHash: input.revision.compiledHash,
    compilerVersion: input.revision.compilerVersion,
    target: input.revision.target,
    briefMarkdown: input.revision.briefMarkdown,
    briefJson: input.revision.briefJson,
    tokenEstimate: input.revision.tokenEstimate,
    sourceNodeIds: input.revision.sourceNodeIds,
    sessionId: null,
    createdAt: input.revision.createdAt,
  });

  const [row] = await tx
    .insert(agentSessions)
    .values({
      id: input.session.id,
      projectId: input.session.projectId,
      taskId: input.session.taskId,
      tokenId: input.session.tokenId,
      agentName: input.session.agentName,
      agentHost: input.session.agentHost,
      status: input.session.status,
      contextRevisionId: input.revision.id,
      startedAt: input.session.startedAt,
      finishedAt: input.session.finishedAt,
      lockExpiresAt: input.session.lockExpiresAt,
      lastHeartbeatAt: input.session.lastHeartbeatAt,
    })
    .returning();
  if (!row) {
    throw new Error("insert agent session returned no row");
  }
  const session = toAgentSession(row);
  if (!session) {
    throw new Error("insert agent session returned invalid row");
  }

  await tx
    .update(contextRevisions)
    .set({ sessionId: session.id })
    .where(eq(contextRevisions.id, input.revision.id));

  if (input.session.taskId) {
    if (stolenFrom) {
      await tx
        .update(agentSessions)
        .set({ status: "abandoned", finishedAt: input.now })
        .where(and(eq(agentSessions.id, stolenFrom), eq(agentSessions.status, "active")));
    }
    await tx
      .update(tasks)
      .set({
        lockedBySessionId: session.id,
        lockExpiresAt: session.lockExpiresAt,
        updatedAt: input.now,
      })
      .where(eq(tasks.id, input.session.taskId));
  }

  return { session, stolenFrom };
}

export type WriteTx = Pick<Db, "insert">;

export async function insertDecisionTx(tx: WriteTx, decision: DecisionRecord): Promise<DecisionRecord> {
  const [row] = await tx
    .insert(decisions)
    .values({
      id: decision.id,
      projectId: decision.projectId,
      title: decision.title,
      status: decision.status,
      context: decision.context,
      decision: decision.decision,
      consequences: decision.consequences,
      createdByType: decision.createdByType,
      createdById: decision.createdById,
      supersededBy: decision.supersededBy,
      createdAt: decision.createdAt,
    })
    .returning();
  if (!row) {
    throw new Error("insert decision returned no row");
  }
  if (decision.relatedPaths.length > 0) {
    await tx.insert(decisionPaths).values(
      decision.relatedPaths.map((path) => ({
        decisionId: decision.id,
        repoId: path.repoId,
        path: path.path,
      })),
    );
  }
  if (decision.relatedTaskIds.length > 0) {
    await tx.insert(decisionTasks).values(
      decision.relatedTaskIds.map((taskId) => ({
        decisionId: decision.id,
        taskId,
      })),
    );
  }
  const created = toDecision(row, decision.relatedPaths, decision.relatedTaskIds);
  if (!created) {
    throw new Error("insert decision returned invalid row");
  }
  return created;
}

export function toGithubInstallation(row: typeof githubInstallations.$inferSelect): GithubInstallationRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    installationId: row.installationId,
    accountLogin: row.accountLogin,
    createdAt: row.createdAt,
  };
}

export function toGithubSyncState(row: typeof githubSyncState.$inferSelect): GithubSyncStateRecord {
  return {
    repoId: row.repoId,
    lastCursor: row.lastCursor,
    lastSyncedAt: row.lastSyncedAt,
  };
}

export function toSidecar(row: typeof sidecarConnections.$inferSelect): {
  id: string;
  repoId: string;
  tokenId: string;
  connectedAt: Date;
  lastSeenAt: Date;
} {
  return {
    id: row.id,
    repoId: row.repoId,
    tokenId: row.tokenId,
    connectedAt: row.connectedAt,
    lastSeenAt: row.lastSeenAt,
  };
}
