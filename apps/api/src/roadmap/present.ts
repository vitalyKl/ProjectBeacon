import type {
  ActivityEventRecord,
  MilestoneRecord,
  TaskCommentRecord,
  TaskDependencyRecord,
  TaskRecord,
} from "./types.js";

export function presentMilestone(milestone: MilestoneRecord) {
  return {
    id: milestone.id,
    project_id: milestone.projectId,
    title: milestone.title,
    description: milestone.description,
    status: milestone.status,
    target_date: milestone.targetDate,
    sort_order: milestone.sortOrder,
    created_at: milestone.createdAt.toISOString(),
  };
}

export function presentTask(task: TaskRecord) {
  return {
    id: task.id,
    project_id: task.projectId,
    milestone_id: task.milestoneId,
    parent_id: task.parentId,
    title: task.title,
    description: task.description,
    status: task.status,
    priority: task.priority,
    type: task.type,
    version: task.version,
    assignee_user_id: task.assigneeUserId,
    assignee_agent_name: task.assigneeAgentName,
    agent_brief: task.agentBrief,
    linked_paths: task.linkedPaths.map((path) => ({ repo_id: path.repo_id, path: path.path })),
    github_issue_id: task.githubIssueId === null ? null : task.githubIssueId.toString(),
    locked_by_session_id: task.lockedBySessionId,
    lock_expires_at: task.lockExpiresAt ? task.lockExpiresAt.toISOString() : null,
    deleted_at: task.deletedAt ? task.deletedAt.toISOString() : null,
    created_at: task.createdAt.toISOString(),
    updated_at: task.updatedAt.toISOString(),
  };
}

export function presentComment(comment: TaskCommentRecord) {
  return {
    id: comment.id,
    task_id: comment.taskId,
    author_type: comment.authorType,
    author_id: comment.authorId,
    body: comment.body,
    created_at: comment.createdAt.toISOString(),
  };
}

export function presentDependency(dependency: TaskDependencyRecord) {
  return {
    from_task_id: dependency.fromTaskId,
    to_task_id: dependency.toTaskId,
    type: dependency.type,
  };
}

export function presentActivity(event: ActivityEventRecord) {
  return {
    id: event.id,
    project_id: event.projectId,
    object_type: event.objectType,
    object_id: event.objectId,
    actor_type: event.actorType,
    actor_id: event.actorId,
    verb: event.verb,
    payload: event.payload,
    created_at: event.createdAt.toISOString(),
  };
}
