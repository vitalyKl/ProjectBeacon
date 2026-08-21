import { activityEvents, agentSessions, handoffs, idempotencyKeys, tasks, type Db } from "@beacon/db";
import { and, desc, eq, sql } from "drizzle-orm";
import { IDEMPOTENCY_TTL_MS } from "../../roadmap/types.js";
import { isLockActive, LOCK_TTL_MS, SessionNotActiveError, TaskLockedError, finishWorkActivities } from "../../sessions/types.js";
import type { TaskStatus, TaskRecord, IdempotencyActorType } from "../../roadmap/types.js";
import type { AgentSessionRecord, StartWorkInput, StartWorkWriteResult, FinishWorkInput, FinishWorkResult, HandoffRecord } from "../../sessions/types.js";
import { toTask, toAgentSession, toHandoff, startWorkInTx, IDEMPOTENCY_LOCK_NS } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { WorkStore } from "../../sessions/store.js";

export function withDbWork<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<WorkStore> {
  return class DbWork extends Base {
  async withIdempotency<T>(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: () => Promise<T>,
  ): Promise<T> {
    return this.db.transaction((tx) =>
      this.writeTx.run(tx as unknown as Db, async () => {
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
          return existing.response as T;
        }
        if (existing) {
          await tx.delete(idempotencyKeys).where(keyMatch);
        }

        const response = await produce();
        await tx.insert(idempotencyKeys).values({
          actorType,
          actorId,
          key,
          response,
          createdAt: now,
        });
        return response;
      }),
    );
  }

  async findAgentSessionById(id: string): Promise<AgentSessionRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(agentSessions)
      .where(eq(agentSessions.id, id))
      .limit(1);
    return row ? toAgentSession(row) : undefined;
  }

  async listAgentSessions(projectId: string): Promise<AgentSessionRecord[]> {
    const rows = await this.db
      .select()
      .from(agentSessions)
      .where(eq(agentSessions.projectId, projectId))
      .orderBy(desc(agentSessions.startedAt), desc(agentSessions.id));
    return rows.flatMap((row) => {
      const session = toAgentSession(row);
      return session ? [session] : [];
    });
  }

  async heartbeatSession(id: string, now: Date): Promise<AgentSessionRecord | undefined> {
    return this.db.transaction(async (tx) => {
      const [current] = await tx
        .select()
        .from(agentSessions)
        .where(eq(agentSessions.id, id))
        .limit(1)
        .for("update");
      if (!current) {
        return undefined;
      }
      const mapped = toAgentSession(current);
      if (!mapped || mapped.status !== "active") {
        return mapped;
      }
      const lockExpiresAt = new Date(now.getTime() + LOCK_TTL_MS);
      const [row] = await tx
        .update(agentSessions)
        .set({
          lastHeartbeatAt: now,
          lockExpiresAt,
        })
        .where(eq(agentSessions.id, id))
        .returning();
      if (current.taskId) {
        await tx
          .update(tasks)
          .set({ lockExpiresAt })
          .where(and(eq(tasks.id, current.taskId), eq(tasks.lockedBySessionId, id)));
      }
      return row ? toAgentSession(row) : mapped;
    });
  }

  async finishWork(input: FinishWorkInput): Promise<FinishWorkResult | undefined> {
    return this.db.transaction(async (tx) => {
      const [sessionRow] = await tx
        .select()
        .from(agentSessions)
        .where(eq(agentSessions.id, input.sessionId))
        .limit(1)
        .for("update");
      if (!sessionRow) {
        return undefined;
      }
      const session = toAgentSession(sessionRow);
      if (!session) {
        return undefined;
      }
      if (session.status !== "active") {
        throw new SessionNotActiveError(session);
      }

      let task: TaskRecord | null = null;
      let lockReleased = false;
      let previousStatus: TaskStatus | null = null;
      if (session.taskId) {
        const [current] = await tx
          .select()
          .from(tasks)
          .where(eq(tasks.id, session.taskId))
          .limit(1)
          .for("update");
        if (current && !current.deletedAt) {
          const mapped = toTask(current);
          if (
            current.lockedBySessionId &&
            current.lockedBySessionId !== session.id &&
            isLockActive(mapped, input.now)
          ) {
            throw new TaskLockedError(mapped);
          }
          previousStatus = current.status as TaskStatus;
          lockReleased = current.lockedBySessionId === session.id;
          const [updated] = await tx
            .update(tasks)
            .set({
              status: input.taskStatus,
              version: current.version + 1,
              updatedAt: input.now,
              ...(input.howToCheck.trim().length > 0 ? { howToCheck: input.howToCheck } : {}),
              ...(lockReleased ? { lockedBySessionId: null, lockExpiresAt: null } : {}),
            })
            .where(eq(tasks.id, current.id))
            .returning();
          if (updated) {
            task = toTask(updated);
          }
        }
      }

      const [handoffRow] = await tx
        .insert(handoffs)
        .values({
          id: input.handoffId,
          sessionId: session.id,
          taskId: session.taskId,
          summary: input.summary,
          nextSteps: input.nextSteps,
          filesTouched: input.filesTouched,
          openQuestions: input.openQuestions,
          createdAt: input.now,
        })
        .returning();
      if (!handoffRow) {
        throw new Error("insert handoff returned no row");
      }

      const [finishedRow] = await tx
        .update(agentSessions)
        .set({
          status: "finished",
          finishedAt: input.now,
        })
        .where(and(eq(agentSessions.id, session.id), eq(agentSessions.status, "active")))
        .returning();
      const finished = finishedRow ? toAgentSession(finishedRow) : undefined;
      if (!finished) {
        throw new SessionNotActiveError(session);
      }

      const result = {
        session: finished,
        handoff: toHandoff(handoffRow),
        task,
        lockReleased,
        previousStatus,
      };
      for (const event of finishWorkActivities({
        session: result.session,
        task: result.task,
        previousStatus: result.previousStatus,
        lockReleased: result.lockReleased,
        taskStatus: input.taskStatus,
        actorType: input.actorType,
        actorId: input.actorId,
        now: input.now,
      })) {
        await tx.insert(activityEvents).values({
          id: event.id,
          projectId: event.projectId,
          objectType: event.objectType,
          objectId: event.objectId,
          actorType: event.actorType,
          actorId: event.actorId,
          verb: event.verb,
          payload: event.payload,
          createdAt: event.createdAt,
        });
      }
      return result;
    });
  }

  async startWork(input: StartWorkInput): Promise<StartWorkWriteResult> {
    const bound = this.writeTx.getStore();
    if (bound) {
      return startWorkInTx(bound, input);
    }
    return this.db.transaction((tx) => startWorkInTx(tx as unknown as Db, input));
  }

  async findLatestHandoffByTaskId(taskId: string): Promise<HandoffRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(handoffs)
      .where(eq(handoffs.taskId, taskId))
      .orderBy(desc(handoffs.createdAt), desc(handoffs.id))
      .limit(1);
    return row ? toHandoff(row) : undefined;
  }
  } as TBase & Ctor<WorkStore>;
}
