import { activityEvents, agentSessions, approvalRequests, contextRevisions, idempotencyKeys, labels, projects, tasks, userSessions, githubSyncState, sidecarConnections } from "@beacon/db";
import { and, asc, eq, inArray, isNull, sql, lte, lt, or } from "drizzle-orm";
import { ACTIVITY_DELETE_BATCH, ACTIVITY_RETENTION_MS, BRIEF_RETENTION_PER_PROJECT, IDEMPOTENCY_RETENTION_MS, USER_SESSION_RETENTION_MS, decideExpiredLocks } from "../../jobs/policy.js";
import type { ExpireLocksCounts, RetentionCounts } from "../../jobs/policy.js";
import { seedDefaultProjectLabels } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { JobStore } from "../../jobs/store.js";

export function withDbJobs<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<JobStore> {
  return class DbJobs extends Base {
  async backfillEmptyProjectLabelCatalogs(): Promise<number> {
    return this.db.transaction(async (tx) => {
      const labeled = await tx.select({ projectId: labels.projectId }).from(labels);
      const labeledIds = new Set(labeled.map((row) => row.projectId));
      const rows = await tx
        .select({ id: projects.id, createdAt: projects.createdAt })
        .from(projects)
        .where(isNull(projects.deletedAt));
      let count = 0;
      for (const project of rows) {
        if (labeledIds.has(project.id)) {
          continue;
        }
        await seedDefaultProjectLabels(tx, project.id, project.createdAt);
        count += 1;
      }
      return count;
    });
  }

  async runRetention(now: Date): Promise<RetentionCounts> {
    const activityCutoff = new Date(now.getTime() - ACTIVITY_RETENTION_MS);
    let activityDeleted = 0;
    for (;;) {
      const batch = await this.db
        .select({ id: activityEvents.id })
        .from(activityEvents)
        .where(lt(activityEvents.createdAt, activityCutoff))
        .orderBy(asc(activityEvents.createdAt), asc(activityEvents.id))
        .limit(ACTIVITY_DELETE_BATCH);
      if (batch.length === 0) {
        break;
      }
      const deleted = await this.db
        .delete(activityEvents)
        .where(
          inArray(
            activityEvents.id,
            batch.map((row) => row.id),
          ),
        )
        .returning({ id: activityEvents.id });
      activityDeleted += deleted.length;
      if (batch.length < ACTIVITY_DELETE_BATCH) {
        break;
      }
    }

    const ranked = this.db
      .select({
        id: contextRevisions.id,
        rank: sql<number>`row_number() over (partition by ${contextRevisions.projectId} order by ${contextRevisions.createdAt} desc, ${contextRevisions.id} desc)`.as(
          "rank",
        ),
      })
      .from(contextRevisions)
      .as("ranked_briefs");
    const staleBriefs = await this.db
      .select({ id: ranked.id })
      .from(ranked)
      .where(sql`${ranked.rank} > ${BRIEF_RETENTION_PER_PROJECT}`);
    let briefsDeleted = 0;
    if (staleBriefs.length > 0) {
      const staleBriefIds = staleBriefs.map((row) => row.id);
      await this.db
        .update(agentSessions)
        .set({ contextRevisionId: null })
        .where(inArray(agentSessions.contextRevisionId, staleBriefIds));
      const deletedBriefs = await this.db
        .delete(contextRevisions)
        .where(inArray(contextRevisions.id, staleBriefIds))
        .returning({ id: contextRevisions.id });
      briefsDeleted = deletedBriefs.length;
    }

    const idempotencyCutoff = new Date(now.getTime() - IDEMPOTENCY_RETENTION_MS);
    const deletedKeys = await this.db
      .delete(idempotencyKeys)
      .where(lt(idempotencyKeys.createdAt, idempotencyCutoff))
      .returning({ key: idempotencyKeys.key });

    const sessionCutoff = new Date(now.getTime() - USER_SESSION_RETENTION_MS);
    const deletedSessions = await this.db
      .delete(userSessions)
      .where(
        or(lte(userSessions.expiresAt, sessionCutoff), lte(userSessions.revokedAt, sessionCutoff)),
      )
      .returning({ id: userSessions.id });

    return {
      activityDeleted,
      briefsDeleted,
      idempotencyDeleted: deletedKeys.length,
      sessionsDeleted: deletedSessions.length,
    };
  }

  async expireLocks(now: Date): Promise<ExpireLocksCounts> {
    return this.db.transaction(async (tx) => {
      const expiredTaskHolders = await tx
        .select({
          id: tasks.id,
          lockedBySessionId: tasks.lockedBySessionId,
        })
        .from(tasks)
        .where(and(sql`${tasks.lockedBySessionId} IS NOT NULL`, lte(tasks.lockExpiresAt, now)));
      const holderIds = expiredTaskHolders.flatMap((task) =>
        task.lockedBySessionId ? [task.lockedBySessionId] : [],
      );

      const candidateSessions = await tx
        .select({
          id: agentSessions.id,
          status: agentSessions.status,
          lockExpiresAt: agentSessions.lockExpiresAt,
          taskId: agentSessions.taskId,
        })
        .from(agentSessions)
        .where(
          or(
            and(eq(agentSessions.status, "active"), lte(agentSessions.lockExpiresAt, now)),
            holderIds.length > 0 ? inArray(agentSessions.id, holderIds) : sql`false`,
          ),
        )
        .orderBy(asc(agentSessions.id))
        .for("update");

      const candidateTaskIds = new Set<string>();
      for (const session of candidateSessions) {
        if (session.taskId) {
          candidateTaskIds.add(session.taskId);
        }
      }
      for (const task of expiredTaskHolders) {
        candidateTaskIds.add(task.id);
      }

      const lockedTasks =
        candidateTaskIds.size === 0
          ? []
          : await tx
              .select({
                id: tasks.id,
                lockedBySessionId: tasks.lockedBySessionId,
                lockExpiresAt: tasks.lockExpiresAt,
              })
              .from(tasks)
              .where(inArray(tasks.id, [...candidateTaskIds]))
              .orderBy(asc(tasks.id))
              .for("update");

      const decided = decideExpiredLocks(candidateSessions, lockedTasks, now);

      let sessionsAbandoned = 0;
      if (decided.sessionIds.length > 0) {
        const abandoned = await tx
          .update(agentSessions)
          .set({ status: "abandoned", finishedAt: now })
          .where(
            and(
              eq(agentSessions.status, "active"),
              inArray(agentSessions.id, decided.sessionIds),
              lte(agentSessions.lockExpiresAt, now),
            ),
          )
          .returning({ id: agentSessions.id });
        sessionsAbandoned = abandoned.length;
      }

      let locksReleased = 0;
      if (decided.taskIds.length > 0) {
        const released = await tx
          .update(tasks)
          .set({ lockedBySessionId: null, lockExpiresAt: null })
          .where(inArray(tasks.id, decided.taskIds))
          .returning({ id: tasks.id });
        locksReleased = released.length;
      }

      return { locksReleased, sessionsAbandoned };
    });
  }

  async listSidecarConnections(): Promise<
    Array<{ id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date }>
  > {
    const rows = await this.db.select().from(sidecarConnections);
    return rows.map((row) => ({
      id: row.id,
      repoId: row.repoId,
      tokenId: row.tokenId,
      connectedAt: row.connectedAt,
      lastSeenAt: row.lastSeenAt,
    }));
  }

  async countPendingApprovals(): Promise<number> {
    const [row] = await this.db
      .select({ value: sql<number>`count(*)::int` })
      .from(approvalRequests)
      .where(eq(approvalRequests.status, "pending"));
    return row?.value ?? 0;
  }

  async githubSyncLagSeconds(now: Date): Promise<number> {
    const [row] = await this.db
      .select({ lastSyncedAt: githubSyncState.lastSyncedAt })
      .from(githubSyncState)
      .orderBy(asc(githubSyncState.lastSyncedAt))
      .limit(1);
    if (!row?.lastSyncedAt) {
      return 0;
    }
    return Math.max(0, Math.floor((now.getTime() - row.lastSyncedAt.getTime()) / 1000));
  }
  } as TBase & Ctor<JobStore>;
}
