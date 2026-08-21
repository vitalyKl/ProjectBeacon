import { activityEvents, milestones, taskComments, taskDependencies, taskLabels, tasks } from "@beacon/db";
import { and, asc, desc, eq, inArray, isNull, sql } from "drizzle-orm";
import { wouldCreateCycle } from "../../roadmap/cycle.js";
import { applyTaskPatch } from "../../roadmap/patch.js";
import { DependencyCycleError, VersionConflictError } from "../../roadmap/types.js";
import { UniqueViolationError } from "../errors.js";
import type { Db } from "@beacon/db";
import type { MilestoneRecord, MilestonePatch, TaskRecord, TaskPatch, TaskCommentRecord, TaskDependencyRecord, ActivityEventRecord } from "../../roadmap/types.js";
import { uniqueIds, uniqueConstraint, toMilestone, toTask, toComment, toDependency, toActivity, DEPENDENCY_LOCK_NS } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { RoadmapStore } from "../../roadmap/store.js";

export function withDbRoadmap<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<RoadmapStore> {
  return class DbRoadmap extends Base {
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

  async updateMilestone(id: string, patch: MilestonePatch): Promise<MilestoneRecord | undefined> {
    const [current] = await this.db.select().from(milestones).where(eq(milestones.id, id)).limit(1);
    if (!current) {
      return undefined;
    }
    // Drizzle throws on .set({}) — empty PATCH is a no-op, not a 500.
    const set = {
      ...(patch.title !== undefined ? { title: patch.title } : {}),
      ...(patch.description !== undefined ? { description: patch.description } : {}),
      ...(patch.status !== undefined ? { status: patch.status } : {}),
      ...(patch.targetDate !== undefined ? { targetDate: patch.targetDate } : {}),
      ...(patch.sortOrder !== undefined ? { sortOrder: patch.sortOrder } : {}),
    };
    if (Object.keys(set).length === 0) {
      return toMilestone(current);
    }
    const [row] = await this.db
      .update(milestones)
      .set(set)
      .where(eq(milestones.id, id))
      .returning();
    return row ? toMilestone(row) : undefined;
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

  async createTask(task: TaskRecord, labelIds: string[] = []): Promise<TaskRecord> {
    const run = async (db: Db) => {
      const [row] = await db
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
          howToCheck: task.howToCheck,
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
      const unique = uniqueIds(labelIds);
      if (unique.length > 0) {
        await db.insert(taskLabels).values(unique.map((labelId) => ({ taskId: task.id, labelId })));
      }
      return toTask(row);
    };
    const bound = this.writeTx.getStore();
    if (bound) {
      return run(bound);
    }
    if (labelIds.length > 0) {
      return this.db.transaction((tx) => run(tx as unknown as Db));
    }
    return run(this.db);
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
      const next = applyTaskPatch(toTask(current), patch);
      let row: typeof current | undefined;
      try {
        [row] = await tx
          .update(tasks)
          .set({
            title: next.title,
            description: next.description,
            status: next.status,
            type: next.type,
            priority: next.priority,
            milestoneId: next.milestoneId,
            parentId: next.parentId,
            assigneeUserId: next.assigneeUserId,
            assigneeAgentName: next.assigneeAgentName,
            agentBrief: next.agentBrief,
            howToCheck: next.howToCheck,
            linkedPaths: next.linkedPaths,
            githubIssueId: next.githubIssueId,
            ...(options?.releaseLock ? { lockedBySessionId: null, lockExpiresAt: null } : {}),
            version: current.version + 1,
            updatedAt,
          })
          .where(and(eq(tasks.id, id), eq(tasks.version, expectedVersion), isNull(tasks.deletedAt)))
          .returning();
      } catch (error) {
        if (uniqueConstraint(error)) {
          throw new UniqueViolationError("tasks_project_github_issue_id_unique");
        }
        throw error;
      }
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
    const [row] = await this.writeDb()
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

  async listDependencies(
    projectId: string,
  ): Promise<(TaskDependencyRecord & { createdAt: Date })[]> {
    const projectTasks = await this.db
      .select({ id: tasks.id, createdAt: tasks.createdAt })
      .from(tasks)
      .where(and(eq(tasks.projectId, projectId), isNull(tasks.deletedAt)));
    if (projectTasks.length === 0) {
      return [];
    }
    const createdAtById = new Map(projectTasks.map((task) => [task.id, task.createdAt]));
    const ids = projectTasks.map((task) => task.id);
    const rows = await this.db
      .select()
      .from(taskDependencies)
      .where(
        and(inArray(taskDependencies.fromTaskId, ids), inArray(taskDependencies.toTaskId, ids)),
      );
    return rows.flatMap((row) => {
      const createdAt = createdAtById.get(row.fromTaskId);
      if (!createdAt) {
        return [];
      }
      return [{ ...toDependency(row), createdAt }];
    });
  }

  async writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord> {
    const [row] = await this.writeDb()
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

  async listComments(taskId: string): Promise<TaskCommentRecord[]> {
    const rows = await this.db
      .select()
      .from(taskComments)
      .where(eq(taskComments.taskId, taskId))
      .orderBy(asc(taskComments.createdAt), asc(taskComments.id));
    return rows.map(toComment);
  }
  } as TBase & Ctor<RoadmapStore>;
}
