export type ApiError = {
  status: number;
  code?: string;
  message: string;
  body: unknown;
};

export class WorkerApiError extends Error {
  override readonly name = "WorkerApiError";
  readonly status: number;
  readonly code?: string;
  readonly body: unknown;

  constructor(error: ApiError) {
    super(error.message);
    this.status = error.status;
    this.code = error.code;
    this.body = error.body;
  }
}

export type MilestoneView = {
  id: string;
  title: string;
};

export type TaskView = {
  id: string;
  title: string;
  milestone_id: string | null;
};

export type WorkerApi = {
  getRepo(repoId: string): Promise<RepoView>;
  importContext(
    projectId: string,
    repoId: string,
    files: { path: string; content: string }[],
  ): Promise<unknown>;
  listMilestones(projectId: string): Promise<MilestoneView[]>;
  createMilestone(
    projectId: string,
    body: { title: string; description?: string },
  ): Promise<{ id: string }>;
  listTasks(projectId: string): Promise<TaskView[]>;
  createTask(
    projectId: string,
    body: {
      title: string;
      description?: string;
      milestone_id?: string | null;
      type?: string;
      linked_paths?: { repo_id: string; path: string }[];
    },
    idempotencyKey: string,
  ): Promise<{ id: string }>;
};

export type RepoView = {
  id: string;
  project_id: string;
  provider: string;
  local_root_hint: string | null;
  index_mode: string;
};

export function createWorkerApi(options: {
  apiUrl: string;
  token: string;
  fetchImpl?: typeof fetch;
}): WorkerApi {
  const fetchImpl = options.fetchImpl ?? fetch;
  const base = options.apiUrl.replace(/\/+$/, "");

  async function request<T>(
    method: string,
    path: string,
    init: { body?: unknown; idempotencyKey?: string } = {},
  ): Promise<T> {
    const headers: Record<string, string> = {
      authorization: `Bearer ${options.token}`,
      accept: "application/json",
    };
    if (init.body !== undefined) {
      headers["content-type"] = "application/json";
    }
    if (init.idempotencyKey) {
      headers["idempotency-key"] = init.idempotencyKey;
    }
    const res = await fetchImpl(`${base}${path}`, {
      method,
      headers,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
    });
    const text = await res.text();
    let parsed: unknown = undefined;
    if (text.length > 0) {
      try {
        parsed = JSON.parse(text) as unknown;
      } catch {
        parsed = text;
      }
    }
    if (!res.ok) {
      const err =
        parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
          ? (parsed as { error?: { code?: string; message?: string } }).error
          : undefined;
      throw new WorkerApiError({
        status: res.status,
        code: err?.code,
        message: err?.message ?? `request failed: ${res.status}`,
        body: parsed,
      });
    }
    return parsed as T;
  }

  return {
    getRepo(repoId) {
      return request<RepoView>("GET", `/v1/repos/${repoId}`);
    },
    importContext(projectId, repoId, files) {
      return request("POST", `/v1/projects/${projectId}/context/import?repo_id=${repoId}`, {
        body: { files },
      });
    },
    async listMilestones(projectId) {
      const page = await request<{ items: MilestoneView[] }>(
        "GET",
        `/v1/projects/${projectId}/milestones`,
      );
      return page.items;
    },
    createMilestone(projectId, body) {
      return request<{ id: string }>("POST", `/v1/projects/${projectId}/milestones`, { body });
    },
    async listTasks(projectId) {
      const page = await request<{ items: TaskView[] }>("GET", `/v1/projects/${projectId}/tasks`);
      return page.items;
    },
    createTask(projectId, body, idempotencyKey) {
      return request<{ id: string }>("POST", `/v1/projects/${projectId}/tasks`, {
        body,
        idempotencyKey,
      });
    },
  };
}
