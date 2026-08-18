import type { CursorPayload } from "@beacon/shared";

export const TASK_STATUSES = [
  "backlog",
  "ready",
  "in_progress",
  "blocked",
  "in_review",
  "done",
  "canceled",
] as const;

export const TASK_TYPES = ["epic", "story", "task", "bug"] as const;

export const MILESTONE_STATUSES = ["open", "closed"] as const;

export const DEPENDENCY_TYPES = ["blocks", "relates"] as const;

export const COMMENT_AUTHOR_TYPES = ["user", "agent", "system"] as const;

export const IDEMPOTENCY_ACTOR_TYPES = ["token", "user"] as const;

export const IDEMPOTENCY_TTL_MS = 24 * 60 * 60 * 1000;

export type TaskStatus = (typeof TASK_STATUSES)[number];
export type TaskType = (typeof TASK_TYPES)[number];
export type MilestoneStatus = (typeof MILESTONE_STATUSES)[number];
export type DependencyType = (typeof DEPENDENCY_TYPES)[number];
export type CommentAuthorType = (typeof COMMENT_AUTHOR_TYPES)[number];
export type IdempotencyActorType = (typeof IDEMPOTENCY_ACTOR_TYPES)[number];

export type LinkedPath = {
  repo_id: string;
  path: string;
};

export type PageQuery = {
  limit: number;
  cursor?: CursorPayload;
};

export type MilestoneRecord = {
  id: string;
  projectId: string;
  title: string;
  description: string;
  status: MilestoneStatus;
  targetDate: string | null;
  sortOrder: number;
  createdAt: Date;
};

export type TaskRecord = {
  id: string;
  projectId: string;
  milestoneId: string | null;
  parentId: string | null;
  title: string;
  description: string;
  status: TaskStatus;
  priority: number;
  type: TaskType;
  version: number;
  assigneeUserId: string | null;
  assigneeAgentName: string | null;
  agentBrief: string;
  linkedPaths: LinkedPath[];
  githubIssueId: bigint | null;
  lockedBySessionId: string | null;
  lockExpiresAt: Date | null;
  deletedAt: Date | null;
  createdAt: Date;
  updatedAt: Date;
};

export type TaskPatch = {
  title?: string;
  description?: string;
  status?: TaskStatus;
  type?: TaskType;
  priority?: number;
  milestoneId?: string | null;
  parentId?: string | null;
  assigneeUserId?: string | null;
  assigneeAgentName?: string | null;
  agentBrief?: string;
  linkedPaths?: LinkedPath[];
  githubIssueId?: bigint | null;
};

export type TaskCommentRecord = {
  id: string;
  taskId: string;
  authorType: CommentAuthorType;
  authorId: string;
  body: string;
  createdAt: Date;
};

export type TaskDependencyRecord = {
  fromTaskId: string;
  toTaskId: string;
  type: DependencyType;
};

export type ActivityEventRecord = {
  id: string;
  projectId: string;
  objectType: string;
  objectId: string;
  actorType: string;
  actorId: string;
  verb: string;
  payload: Record<string, unknown>;
  createdAt: Date;
};

export class VersionConflictError extends Error {
  override readonly name = "VersionConflictError";
  readonly current: TaskRecord;

  constructor(current: TaskRecord) {
    super("version conflict");
    this.current = current;
  }
}

export class DependencyCycleError extends Error {
  override readonly name = "DependencyCycleError";

  constructor() {
    super("dependency cycle");
  }
}

export function isTaskStatus(value: string): value is TaskStatus {
  return (TASK_STATUSES as readonly string[]).includes(value);
}

export function isTaskType(value: string): value is TaskType {
  return (TASK_TYPES as readonly string[]).includes(value);
}

export function isMilestoneStatus(value: string): value is MilestoneStatus {
  return (MILESTONE_STATUSES as readonly string[]).includes(value);
}

export function isDependencyType(value: string): value is DependencyType {
  return (DEPENDENCY_TYPES as readonly string[]).includes(value);
}
