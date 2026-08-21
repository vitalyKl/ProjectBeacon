import { wouldCreateCycle } from "../../roadmap/cycle.js";
import { applyMilestonePatch, applyTaskPatch } from "../../roadmap/patch.js";
import { DependencyCycleError, VersionConflictError } from "../../roadmap/types.js";
import { UniqueViolationError } from "../errors.js";
import type { MilestoneRecord, MilestonePatch, TaskRecord, TaskPatch, TaskCommentRecord, TaskDependencyRecord, ActivityEventRecord } from "../../roadmap/types.js";
import { cloneMilestone, cloneTask, cloneComment, cloneActivity } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryRoadmap<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryRoadmap extends Base {
  async createMilestone(milestone: MilestoneRecord): Promise<MilestoneRecord> {
    this.milestones.set(milestone.id, cloneMilestone(milestone));
    return cloneMilestone(milestone);
  }

  async updateMilestone(id: string, patch: MilestonePatch): Promise<MilestoneRecord | undefined> {
    const milestone = this.milestones.get(id);
    if (!milestone) {
      return undefined;
    }
    const next = applyMilestonePatch(milestone, patch);
    this.milestones.set(id, next);
    return cloneMilestone(next);
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

  insertTaskUnlocked(task: TaskRecord): TaskRecord {
    this.tasks.set(task.id, cloneTask(task));
    return cloneTask(task);
  }

  insertCommentUnlocked(comment: TaskCommentRecord): TaskCommentRecord {
    this.comments.set(comment.id, cloneComment(comment));
    return cloneComment(comment);
  }

  override insertActivityUnlocked(event: ActivityEventRecord): ActivityEventRecord {
    this.activity.set(event.id, cloneActivity(event));
    return cloneActivity(event);
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
  };
}
