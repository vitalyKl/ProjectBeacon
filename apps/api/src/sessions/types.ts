import type { LinkedPath, TaskRecord, TaskStatus } from "../roadmap/types.js";
import type { ContextRevisionRecord } from "../context/types.js";

export const AGENT_SESSION_STATUSES = ["active", "paused", "finished", "abandoned"] as const;
export const AGENT_HOSTS = ["grok", "claude", "cursor", "codex", "custom"] as const;
export const FINISH_WORK_STATUSES = ["done", "canceled", "in_review", "blocked", "ready"] as const;

export const LOCK_TTL_MS = 4 * 60 * 60 * 1000;
export const HANDOFF_SUMMARY_MIN = 20;

export type AgentSessionStatus = (typeof AGENT_SESSION_STATUSES)[number];
export type AgentHost = (typeof AGENT_HOSTS)[number];
export type FinishWorkStatus = (typeof FINISH_WORK_STATUSES)[number];

export type AgentSessionRecord = {
  id: string;
  projectId: string;
  taskId: string | null;
  tokenId: string | null;
  agentName: string;
  agentHost: AgentHost;
  status: AgentSessionStatus;
  contextRevisionId: string | null;
  startedAt: Date;
  finishedAt: Date | null;
  lockExpiresAt: Date | null;
  lastHeartbeatAt: Date;
};

export type HandoffRecord = {
  id: string;
  sessionId: string;
  taskId: string | null;
  summary: string;
  nextSteps: string;
  filesTouched: LinkedPath[];
  openQuestions: string[];
  createdAt: Date;
};

export type StartWorkInput = {
  session: AgentSessionRecord;
  revision: ContextRevisionRecord;
  steal: boolean;
  now: Date;
};

export type StartWorkWriteResult = {
  session: AgentSessionRecord;
  stolenFrom: string | null;
};

export type FinishWorkInput = {
  sessionId: string;
  handoffId: string;
  summary: string;
  nextSteps: string;
  filesTouched: LinkedPath[];
  openQuestions: string[];
  taskStatus: FinishWorkStatus;
  now: Date;
};

export type FinishWorkResult = {
  session: AgentSessionRecord;
  handoff: HandoffRecord;
  task: TaskRecord | null;
  lockReleased: boolean;
  previousStatus: TaskStatus | null;
};

export class TaskLockedError extends Error {
  override readonly name = "TaskLockedError";
  readonly task: TaskRecord;

  constructor(task: TaskRecord) {
    super("task locked");
    this.task = task;
  }
}

export class InvalidReferenceError extends Error {
  override readonly name = "InvalidReferenceError";
  readonly entity: string;

  constructor(entity: string) {
    super(`${entity} not found`);
    this.entity = entity;
  }
}

export class SessionNotActiveError extends Error {
  override readonly name = "SessionNotActiveError";
  readonly session: AgentSessionRecord;

  constructor(session: AgentSessionRecord) {
    super("session is not active");
    this.session = session;
  }
}

export function isAgentSessionStatus(value: string): value is AgentSessionStatus {
  return (AGENT_SESSION_STATUSES as readonly string[]).includes(value);
}

export function isAgentHost(value: string): value is AgentHost {
  return (AGENT_HOSTS as readonly string[]).includes(value);
}

export function isFinishWorkStatus(value: string): value is FinishWorkStatus {
  return (FINISH_WORK_STATUSES as readonly string[]).includes(value);
}

export function isTerminalTaskStatus(status: string): boolean {
  return status === "done" || status === "canceled";
}

export function lockExpiresAt(now: Date): Date {
  return new Date(now.getTime() + LOCK_TTL_MS);
}

export function isLockActive(task: TaskRecord, now: Date): boolean {
  return Boolean(
    task.lockedBySessionId &&
      task.lockExpiresAt &&
      task.lockExpiresAt.getTime() > now.getTime(),
  );
}
