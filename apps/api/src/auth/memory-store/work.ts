import { UniqueViolationError } from "../errors.js";
import { InvalidReferenceError, isLockActive, LOCK_TTL_MS, SessionNotActiveError, TaskLockedError, finishWorkActivities } from "../../sessions/types.js";
import type { ActivityEventRecord, IdempotencyActorType, TaskRecord } from "../../roadmap/types.js";
import type { ContextRevisionRecord } from "../../context/types.js";
import type { AgentSessionRecord, StartWorkInput, StartWorkWriteResult, FinishWorkInput, FinishWorkResult, HandoffRecord } from "../../sessions/types.js";
import type { AgentSessionRef } from "../../tokens/types.js";
import { cloneActivity, cloneAgentSession, cloneContextRevision, cloneHandoff, cloneTask, idempotencyKey } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryWork<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryWork extends Base {
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

  startWorkUnlocked(input: StartWorkInput): StartWorkWriteResult {
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

  finishWorkUnlocked(input: FinishWorkInput): FinishWorkResult | undefined {
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
  };
}
