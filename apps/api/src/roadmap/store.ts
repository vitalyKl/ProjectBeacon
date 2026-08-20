import type {
  ActivityEventRecord,
  MilestoneRecord,
  TaskCommentRecord,
  TaskDependencyRecord,
  TaskPatch,
  TaskRecord,
} from "./types.js";

export interface RoadmapStore {
  createMilestone(milestone: MilestoneRecord): Promise<MilestoneRecord>;
  listMilestones(projectId: string): Promise<MilestoneRecord[]>;
  findMilestoneById(id: string): Promise<MilestoneRecord | undefined>;
  createTask(task: TaskRecord): Promise<TaskRecord>;
  listTasks(projectId: string): Promise<TaskRecord[]>;
  findTaskById(id: string): Promise<TaskRecord | undefined>;
  updateTask(
    id: string,
    expectedVersion: number,
    patch: TaskPatch,
    updatedAt: Date,
    options?: { releaseLock?: boolean },
  ): Promise<{ task: TaskRecord; lockReleased: boolean } | undefined>;
  softDeleteTask(id: string, deletedAt: Date): Promise<TaskRecord | undefined>;
  createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord>;
  addDependency(dependency: TaskDependencyRecord): Promise<TaskDependencyRecord>;
  writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord>;
  listActivity(
    projectId: string,
    filters?: { objectType?: string; objectId?: string },
  ): Promise<ActivityEventRecord[]>;
  listComments(taskId: string): Promise<TaskCommentRecord[]>;
  listDependencies(projectId: string): Promise<(TaskDependencyRecord & { createdAt: Date })[]>;
}
