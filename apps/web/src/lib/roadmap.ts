import { ApiError, apiFetch, fetchAllPages, parseJson, readApiError, type Page } from "./api";

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

function pageQuery(cursor: string | null): string {
  const params = new URLSearchParams();
  params.set("limit", "100");
  if (cursor) {
    params.set("cursor", cursor);
  }
  return params.toString();
}

async function fetchPage<T>(path: string, fallback: string): Promise<Page<T>> {
  const res = await apiFetch(path);
  if (!res.ok) {
    throw await readApiError(res, fallback);
  }
  return parseJson<Page<T>>(res);
}

export async function fetchProjectMilestones(projectId: string): Promise<PublicMilestone[]> {
  return fetchAllPages((cursor) =>
    fetchPage<PublicMilestone>(
      `/v1/projects/${encodeURIComponent(projectId)}/milestones?${pageQuery(cursor)}`,
      "failed to load milestones",
    ),
  );
}

export async function fetchProjectTasks(projectId: string): Promise<PublicTask[]> {
  return fetchAllPages((cursor) =>
    fetchPage<PublicTask>(
      `/v1/projects/${encodeURIComponent(projectId)}/tasks?${pageQuery(cursor)}`,
      "failed to load tasks",
    ),
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
  return fetchAllPages((cursor) =>
    fetchPage<PublicComment>(
      `/v1/tasks/${encodeURIComponent(taskId)}/comments?${pageQuery(cursor)}`,
      "failed to load comments",
    ),
  );
}

export async function fetchTaskActivity(
  projectId: string,
  taskId: string,
): Promise<PublicActivity[]> {
  return fetchAllPages((cursor) =>
    fetchPage<PublicActivity>(
      `/v1/projects/${encodeURIComponent(projectId)}/activity?object_id=${encodeURIComponent(taskId)}&${pageQuery(cursor)}`,
      "failed to load activity",
    ),
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
