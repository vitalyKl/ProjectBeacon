import { ApiError, apiFetch, fetchAllPages, parseJson, readApiError } from "./api";

export const TASK_STATUSES = [
  "backlog",
  "ready",
  "in_progress",
  "blocked",
  "in_review",
  "done",
  "canceled",
] as const;

export const TASK_TYPES = ["epic", "story", "task", "bug"] as const;

export type TaskStatus = (typeof TASK_STATUSES)[number];
export type TaskType = (typeof TASK_TYPES)[number];

export type LinkedPath = {
  repo_id: string;
  path: string;
};

export type PublicMilestone = {
  id: string;
  project_id: string;
  title: string;
  description: string;
  status: "open" | "closed";
  target_date: string | null;
  sort_order: number;
  created_at: string;
};

export type PublicTask = {
  id: string;
  project_id: string;
  milestone_id: string | null;
  parent_id: string | null;
  title: string;
  description: string;
  status: TaskStatus;
  priority: number;
  type: TaskType;
  version: number;
  assignee_user_id: string | null;
  assignee_agent_name: string | null;
  agent_brief: string;
  linked_paths: LinkedPath[];
  github_issue_id: string | null;
  locked_by_session_id: string | null;
  lock_expires_at: string | null;
  deleted_at: string | null;
  created_at: string;
  updated_at: string;
};

export type PublicComment = {
  id: string;
  task_id: string;
  author_type: "user" | "agent" | "system";
  author_id: string;
  body: string;
  created_at: string;
};

export const DEPENDENCY_TYPES = ["blocks", "relates"] as const;

export type DependencyType = (typeof DEPENDENCY_TYPES)[number];

export type PublicDependency = {
  from_task_id: string;
  to_task_id: string;
  type: DependencyType;
};

export type PublicActivity = {
  id: string;
  project_id: string;
  object_type: string;
  object_id: string;
  actor_type: string;
  actor_id: string;
  verb: string;
  payload: Record<string, unknown>;
  created_at: string;
};

export type SessionBriefPreview = {
  compiled_at?: string;
  budget?: {
    used_estimate?: number;
    overflow?: boolean;
    dropped?: string[];
  };
  sections?: { title: string; body_md: string }[];
  task?: { title: string; status: string } | null;
  milestone?: { title: string; status: string } | null;
  handoff?: { summary: string; next_steps: string } | null;
};

export type CreateTaskInput = {
  title: string;
  description?: string;
  status?: TaskStatus;
  type?: TaskType;
  milestone_id?: string | null;
  assignee_agent_name?: string | null;
};

export type TaskPatchInput = {
  expected_version: number;
  title?: string;
  description?: string;
  status?: TaskStatus;
  type?: TaskType;
  milestone_id?: string | null;
  assignee_agent_name?: string | null;
  agent_brief?: string;
};

export async function fetchProjectMilestones(projectId: string): Promise<PublicMilestone[]> {
  return fetchAllPages<PublicMilestone>(
    `/v1/projects/${encodeURIComponent(projectId)}/milestones`,
    "failed to load milestones",
  );
}

export async function fetchProjectTasks(projectId: string): Promise<PublicTask[]> {
  return fetchAllPages<PublicTask>(
    `/v1/projects/${encodeURIComponent(projectId)}/tasks`,
    "failed to load tasks",
  );
}

export async function fetchProjectDependencies(projectId: string): Promise<PublicDependency[]> {
  return fetchAllPages<PublicDependency>(
    `/v1/projects/${encodeURIComponent(projectId)}/dependencies`,
    "failed to load dependencies",
  );
}

export async function fetchTask(taskId: string): Promise<PublicTask> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(taskId)}`);
  if (!res.ok) {
    throw await readApiError(res, "task not found");
  }
  return parseJson<PublicTask>(res);
}

export async function fetchTaskComments(taskId: string): Promise<PublicComment[]> {
  return fetchAllPages<PublicComment>(
    `/v1/tasks/${encodeURIComponent(taskId)}/comments`,
    "failed to load comments",
  );
}

export async function fetchTaskActivity(
  projectId: string,
  taskId: string,
): Promise<PublicActivity[]> {
  return fetchAllPages<PublicActivity>(
    `/v1/projects/${encodeURIComponent(projectId)}/activity?object_id=${encodeURIComponent(taskId)}`,
    "failed to load activity",
  );
}

export async function createMilestone(
  projectId: string,
  title: string,
  description = "",
  idempotencyKey?: string,
): Promise<PublicMilestone> {
  const headers: HeadersInit = {};
  if (idempotencyKey) {
    headers["idempotency-key"] = idempotencyKey;
  }
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/milestones`, {
    method: "POST",
    headers,
    body: JSON.stringify({ title, description }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create milestone");
  }
  return parseJson<PublicMilestone>(res);
}

export async function createTask(
  projectId: string,
  input: CreateTaskInput,
  idempotencyKey: string,
): Promise<PublicTask> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/tasks`, {
    method: "POST",
    headers: { "idempotency-key": idempotencyKey },
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create task");
  }
  return parseJson<PublicTask>(res);
}

export async function patchTask(taskId: string, input: TaskPatchInput): Promise<PublicTask> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(taskId)}`, {
    method: "PATCH",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update task");
  }
  return parseJson<PublicTask>(res);
}

export async function setTaskStatus(
  taskId: string,
  status: TaskStatus,
  expectedVersion: number,
): Promise<PublicTask> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(taskId)}/status`, {
    method: "POST",
    body: JSON.stringify({ status, expected_version: expectedVersion }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update status");
  }
  return parseJson<PublicTask>(res);
}

export async function createTaskDependency(
  fromTaskId: string,
  toTaskId: string,
  type: DependencyType,
  idempotencyKey: string,
): Promise<PublicDependency> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(fromTaskId)}/dependencies`, {
    method: "POST",
    headers: { "idempotency-key": idempotencyKey },
    body: JSON.stringify({ to_task_id: toTaskId, type }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to add dependency");
  }
  return parseJson<PublicDependency>(res);
}

export async function createTaskComment(
  taskId: string,
  body: string,
  idempotencyKey: string,
): Promise<PublicComment> {
  const res = await apiFetch(`/v1/tasks/${encodeURIComponent(taskId)}/comments`, {
    method: "POST",
    headers: { "idempotency-key": idempotencyKey },
    body: JSON.stringify({ body }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to add comment");
  }
  return parseJson<PublicComment>(res);
}

export async function compileTaskBrief(
  projectId: string,
  taskId: string,
): Promise<SessionBriefPreview> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/context/compile`, {
    method: "POST",
    body: JSON.stringify({ project_id: projectId, task_id: taskId }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to compile brief");
  }
  return parseJson<SessionBriefPreview>(res);
}

export function taskFromConflict(error: ApiError): PublicTask | null {
  const task = error.details["task"];
  if (!task || typeof task !== "object" || Array.isArray(task)) {
    return null;
  }
  const record = task as Partial<PublicTask>;
  if (typeof record.id !== "string" || typeof record.version !== "number") {
    return null;
  }
  return record as PublicTask;
}

export function isTaskLocked(task: PublicTask, now = Date.now()): boolean {
  if (!task.locked_by_session_id || !task.lock_expires_at) {
    return false;
  }
  const expires = Date.parse(task.lock_expires_at);
  return Number.isFinite(expires) && expires > now;
}

export function statusLabel(status: TaskStatus): string {
  switch (status) {
    case "in_progress":
      return "In progress";
    case "in_review":
      return "In review";
    default:
      return status.replaceAll("_", " ");
  }
}
