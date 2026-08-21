import { ACTIVITY_RETENTION_MS, BRIEF_RETENTION_PER_PROJECT, IDEMPOTENCY_RETENTION_MS, idsOlderThanKeep, isUserSessionPastRetention, decideExpiredLocks } from "../../jobs/policy.js";
import type { ContextRevisionRecord } from "../../context/types.js";
import type { ExpireLocksCounts, RetentionCounts } from "../../jobs/policy.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryJobs<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryJobs extends Base {
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

  expireLocksUnlocked(now: Date): ExpireLocksCounts {
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
  };
}
