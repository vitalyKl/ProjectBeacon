import type { TaskPatch, TaskRecord } from "./types.js";

export function applyTaskPatch(task: TaskRecord, patch: TaskPatch): TaskRecord {
  return {
    ...task,
    title: patch.title !== undefined ? patch.title : task.title,
    description: patch.description !== undefined ? patch.description : task.description,
    status: patch.status !== undefined ? patch.status : task.status,
    type: patch.type !== undefined ? patch.type : task.type,
    priority: patch.priority !== undefined ? patch.priority : task.priority,
    milestoneId: patch.milestoneId !== undefined ? patch.milestoneId : task.milestoneId,
    parentId: patch.parentId !== undefined ? patch.parentId : task.parentId,
    assigneeUserId: patch.assigneeUserId !== undefined ? patch.assigneeUserId : task.assigneeUserId,
    assigneeAgentName:
      patch.assigneeAgentName !== undefined ? patch.assigneeAgentName : task.assigneeAgentName,
    agentBrief: patch.agentBrief !== undefined ? patch.agentBrief : task.agentBrief,
    howToCheck: patch.howToCheck !== undefined ? patch.howToCheck : task.howToCheck,
    linkedPaths:
      patch.linkedPaths !== undefined
        ? patch.linkedPaths.map((path) => ({ ...path }))
        : task.linkedPaths.map((path) => ({ ...path })),
    githubIssueId: patch.githubIssueId !== undefined ? patch.githubIssueId : task.githubIssueId,
  };
}
