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
  github_issue_id?: string | null;
  version?: number;
};

export type GithubIssueView = {
  id: string;
  number: number;
  title: string;
  body: string;
  state: string;
  html_url: string;
};

export type WorkerApi = {
  getRepo(repoId: string): Promise<RepoView>;
  reportIndex(
    repoId: string,
    body: { last_indexed_sha?: string | null; last_indexed_at?: string | null },
  ): Promise<unknown>;
  consumeCloneInvalidation(
    repoId: string,
  ): Promise<{ consumed: { id: string; sha: string | null; created_at: string } | null }>;
  listDeletedProjects(): Promise<{ id: string; deleted_at: string | null }[]>;
  projectClonePurge(projectId: string): Promise<{
    project_id: string;
    deleted: boolean;
    repo_ids: string[];
  }>;
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
  listGithubIssues(
    repoId: string,
    query?: { state?: string; q?: string },
  ): Promise<GithubIssueView[]>;
  getGithubIssue(repoId: string, issueNumber: number): Promise<GithubIssueView | undefined>;
  upsertImportedIssues(
    repoId: string,
    issues: Array<{
      github_issue_id: string;
      number: number;
      title: string;
      body: string;
    }>,
    cursor?: string | null,
  ): Promise<unknown>;
  recordGithubInvalidation(
    repoId: string,
    body: { ref?: string; before?: string; after?: string },
  ): Promise<unknown>;
};

export type RepoView = {
  id: string;
  project_id: string;
  provider: string;
  remote_url: string | null;
  default_branch: string;
  installation_id: string | null;
  local_root_hint: string | null;
  index_mode: string;
  remote_url?: string | null;
  github_repo_id?: string | null;
  installation_id?: string | null;
};

const PAGE_LIMIT = 100;
const MAX_PAGES = 100;

function pagedPath(path: string, cursor: string | null): string {
  const query = new URLSearchParams({ limit: String(PAGE_LIMIT) });
  if (cursor) {
    query.set("cursor", cursor);
  }
  return `${path}?${query.toString()}`;
}

async function listAllPages<T>(
  fetchPage: (cursor: string | null) => Promise<{ items: T[]; next_cursor?: string | null }>,
): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  for (let page = 0; page < MAX_PAGES; page += 1) {
    const result = await fetchPage(cursor);
    items.push(...result.items);
    const next = result.next_cursor ?? null;
    if (!next || next === cursor) {
      return items;
    }
    cursor = next;
  }
  return items;
}

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
    reportIndex(repoId, body) {
      return request("POST", `/v1/repos/${repoId}/index`, { body });
    },
    consumeCloneInvalidation(repoId) {
      return request("POST", `/v1/repos/${repoId}/clone-invalidation/consume`);
    },
    async listDeletedProjects() {
      const page = await request<{ items: { id: string; deleted_at: string | null }[] }>(
        "GET",
        "/v1/internal/deleted-projects",
      );
      return page.items;
    },
    projectClonePurge(projectId) {
      return request("GET", `/v1/projects/${projectId}/hosted-clone-purge`);
    },
    importContext(projectId, repoId, files) {
      return request("POST", `/v1/projects/${projectId}/context/import?repo_id=${repoId}`, {
        body: { files },
      });
    },
    async listMilestones(projectId) {
      return listAllPages((cursor) =>
        request<{ items: MilestoneView[]; next_cursor: string | null }>(
          "GET",
          pagedPath(`/v1/projects/${projectId}/milestones`, cursor),
        ),
      );
    },
    createMilestone(projectId, body) {
      return request<{ id: string }>("POST", `/v1/projects/${projectId}/milestones`, { body });
    },
    async listTasks(projectId) {
      return listAllPages((cursor) =>
        request<{ items: TaskView[]; next_cursor: string | null }>(
          "GET",
          pagedPath(`/v1/projects/${projectId}/tasks`, cursor),
        ),
      );
    },
    createTask(projectId, body, idempotencyKey) {
      return request<{ id: string }>("POST", `/v1/projects/${projectId}/tasks`, {
        body,
        idempotencyKey,
      });
    },
    async listGithubIssues(repoId, query) {
      return listAllPages((cursor) => {
        const params = new URLSearchParams();
        if (query?.state) {
          params.set("state", query.state);
        }
        if (query?.q) {
          params.set("q", query.q);
        }
        if (cursor) {
          params.set("cursor", cursor);
        }
        const suffix = params.toString();
        return request<{ items: GithubIssueView[]; next_cursor: string | null }>(
          "GET",
          `/v1/repos/${repoId}/github/issues${suffix ? `?${suffix}` : ""}`,
        );
      });
    },
    async getGithubIssue(repoId, issueNumber) {
      const items = await this.listGithubIssues(repoId, { state: "all" });
      return items.find((item) => item.number === issueNumber);
    },
    upsertImportedIssues(repoId, issues, cursor) {
      return request("POST", `/v1/repos/${repoId}/github/imported-issues`, {
        body: { issues, cursor: cursor ?? null },
      });
    },
    recordGithubInvalidation(repoId, body) {
      return request("POST", `/v1/repos/${repoId}/github/invalidations`, { body });
    },
  };
}
