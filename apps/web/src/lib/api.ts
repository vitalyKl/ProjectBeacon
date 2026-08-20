export type PublicUser = {
  id: string;
  login: string;
  email: string | null;
  name: string | null;
  avatar_url: string | null;
};

export type PublicOrg = {
  id: string;
  slug: string;
  name: string;
  kind: "personal" | "team";
};

export type PublicMe = PublicUser & {
  personal_org: PublicOrg | null;
  orgs: PublicOrg[];
};

export type PublicProject = {
  id: string;
  org_id: string;
  slug: string;
  name: string;
  description: string;
  visibility: "private";
  default_repo_id: string | null;
};

export type PublicRepo = {
  id: string;
  project_id?: string;
  provider?: string;
  remote_url?: string | null;
  default_branch?: string;
  local_root_hint?: string | null;
  index_mode: IndexMode;
  sidecar_connected?: boolean;
  worker_index_connected?: boolean;
  last_indexed_at: string | null;
  last_indexed_sha?: string | null;
};

export type PublicToken = {
  id: string;
  project_id: string;
  name: string;
  prefix: string;
  scopes: string[];
  token?: string;
};

export type PublicContextNode = {
  id: string;
  project_id: string;
  scope_type: string;
  path: string;
  sections: { id: string; key?: string; title: string; body_md: string; ordinal: number }[];
};

export type ApiErrorBody = {
  error?: {
    code?: string;
    message?: string;
    details?: Record<string, unknown>;
  };
};

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly details: Record<string, unknown>;

  constructor(
    status: number,
    code: string,
    message: string,
    details: Record<string, unknown> = {},
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

function messageFromBody(body: ApiErrorBody | undefined, fallback: string): string {
  return body?.error?.message ?? fallback;
}

export async function parseJson<T>(res: Response): Promise<T> {
  if (res.status === 204) {
    return undefined as T;
  }
  return (await res.json()) as T;
}

export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (init.body !== undefined && !headers.has("content-type")) {
    headers.set("content-type", "application/json");
  }
  return fetch(path, {
    ...init,
    headers,
    credentials: "same-origin",
  });
}

export async function readApiError(res: Response, fallback: string): Promise<ApiError> {
  let body: ApiErrorBody | undefined;
  try {
    body = (await res.json()) as ApiErrorBody;
  } catch {
    body = undefined;
  }
  return new ApiError(
    res.status,
    body?.error?.code ?? "unauthorized",
    messageFromBody(body, fallback),
    body?.error?.details ?? {},
  );
}

export type Page<T> = {
  items: T[];
  next_cursor: string | null;
};

export async function fetchAllPages<T>(path: string, fallback: string): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  for (;;) {
    const res = await apiFetch(withPageParams(path, cursor));
    if (!res.ok) {
      throw await readApiError(res, fallback);
    }
    const body = await parseJson<PageBody<T>>(res);
    items.push(...(body.items ?? []));
    if (!body.next_cursor) {
      return items;
    }
    cursor = body.next_cursor;
  }
}

export type ProjectRole = "admin" | "write" | "read";

export type TokenTtl = "7d" | "90d" | "1y" | "none";

export type IndexMode = "sidecar" | "bind_mount" | "hosted_clone" | "both";

export const DEFAULT_TOKEN_SCOPES = [
  "project:read",
  "context:read",
  "context:write",
  "tasks:read",
  "tasks:write",
  "decisions:read",
  "decisions:write",
  "sessions:write",
] as const;

export const CODE_READ_SCOPE = "code:read";

export type PublicProjectMember = {
  project_id: string;
  user_id: string;
  role: ProjectRole;
  login: string | null;
  email: string | null;
  name: string | null;
  created_at: string;
};

export type PublicProjectInvite = {
  id: string;
  project_id: string;
  email: string | null;
  github_login: string | null;
  role: ProjectRole;
  invited_by: string;
  expires_at: string;
  accepted_at: string | null;
};

export type PublicApiToken = {
  id: string;
  project_id: string;
  name: string;
  prefix: string;
  scopes: string[];
  created_by: string | null;
  last_used_at: string | null;
  expires_at: string | null;
  revoked_at: string | null;
  created_at: string;
  token?: string;
};

export type PublicAgentSession = {
  id: string;
  project_id: string;
  task_id: string | null;
  agent: {
    id: string;
    name: string;
    host: string;
  };
  status: string;
  context_revision_id: string | null;
  started_at: string;
  finished_at: string | null;
  lock_expires_at: string | null;
  last_heartbeat_at: string;
};

export type PublicActivityEvent = {
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

export type PageBody<T> = {
  items: T[];
  next_cursor: string | null;
};

export async function fetchMe(): Promise<PublicMe | null> {
  const res = await apiFetch("/v1/me");
  if (res.status === 401) {
    return null;
  }
  if (!res.ok) {
    throw await readApiError(res, "failed to load session");
  }
  return parseJson<PublicMe>(res);
}

export async function fetchOrgProjects(orgId: string): Promise<PublicProject[]> {
  const res = await apiFetch(`/v1/orgs/${encodeURIComponent(orgId)}/projects`);
  if (!res.ok) {
    throw await readApiError(res, "failed to load projects");
  }
  const body = await parseJson<{ items: PublicProject[] }>(res);
  return body.items;
}

export async function createOrgProject(
  orgId: string,
  input: { slug: string; name: string; description?: string },
): Promise<PublicProject> {
  const res = await apiFetch(`/v1/orgs/${encodeURIComponent(orgId)}/projects`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create project");
  }
  return parseJson<PublicProject>(res);
}

export async function loginLocal(login: string, password: string): Promise<PublicUser> {
  const res = await apiFetch("/v1/auth/login", {
    method: "POST",
    body: JSON.stringify({ login, password }),
  });
  if (!res.ok) {
    throw await readApiError(res, "invalid login or password");
  }
  return parseJson<PublicUser>(res);
}

export async function bootstrapLocal(
  token: string,
  login: string,
  password: string,
): Promise<PublicUser> {
  const res = await apiFetch("/v1/auth/bootstrap", {
    method: "POST",
    headers: {
      authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({ login, password }),
  });
  if (!res.ok) {
    throw await readApiError(res, "bootstrap failed");
  }
  return parseJson<PublicUser>(res);
}

export async function logoutSession(): Promise<void> {
  const res = await apiFetch("/v1/auth/logout", { method: "POST" });
  if (!res.ok) {
    throw await readApiError(res, "logout failed");
  }
}

export function githubAuthorizeUrl(clientId: string, redirectTo: string): string {
  const url = new URL("https://github.com/login/oauth/authorize");
  url.searchParams.set("client_id", clientId);
  url.searchParams.set("redirect_uri", redirectTo);
  url.searchParams.set("scope", "read:user user:email");
  return url.toString();
}

function withPageParams(path: string, cursor: string | null): string {
  const url = new URL(path, "http://beacon.local");
  url.searchParams.set("limit", "100");
  if (cursor) {
    url.searchParams.set("cursor", cursor);
  }
  return `${url.pathname}${url.search}`;
}

export async function updateProject(
  projectId: string,
  patch: { name?: string; description?: string },
): Promise<PublicProject> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}`, {
    method: "PATCH",
    body: JSON.stringify(patch),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update project");
  }
  return parseJson<PublicProject>(res);
}

export async function fetchProjectMembers(projectId: string): Promise<PublicProjectMember[]> {
  return fetchAllPages<PublicProjectMember>(
    `/v1/projects/${encodeURIComponent(projectId)}/members`,
    "failed to load members",
  );
}

export async function upsertProjectMember(
  projectId: string,
  input: { user_id: string; role: ProjectRole },
): Promise<PublicProjectMember> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/members`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update member");
  }
  return parseJson<PublicProjectMember>(res);
}

export async function createProjectInvite(
  projectId: string,
  input: { email?: string; github_login?: string; role: ProjectRole },
): Promise<PublicProjectInvite> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/invites`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create invite");
  }
  return parseJson<PublicProjectInvite>(res);
}

export async function fetchProjectTokens(projectId: string): Promise<PublicApiToken[]> {
  return fetchAllPages<PublicApiToken>(
    `/v1/projects/${encodeURIComponent(projectId)}/tokens`,
    "failed to load tokens",
  );
}

export async function createProjectToken(
  projectId: string,
  input: { name: string; scopes: string[]; ttl: TokenTtl; confirm?: boolean },
): Promise<PublicApiToken> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/tokens`, {
    method: "POST",
    body: JSON.stringify({
      name: input.name,
      scopes: input.scopes,
      ttl: input.ttl,
      ...(input.ttl === "none" ? { confirm: true } : {}),
    }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create token");
  }
  return parseJson<PublicApiToken>(res);
}

export async function revokeProjectToken(tokenId: string): Promise<PublicApiToken> {
  const res = await apiFetch(`/v1/tokens/${encodeURIComponent(tokenId)}/revoke`, {
    method: "POST",
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to revoke token");
  }
  return parseJson<PublicApiToken>(res);
}

export async function fetchProjectSessions(projectId: string): Promise<PublicAgentSession[]> {
  return fetchAllPages<PublicAgentSession>(
    `/v1/projects/${encodeURIComponent(projectId)}/sessions`,
    "failed to load sessions",
  );
}

export async function fetchProjectActivity(projectId: string): Promise<PublicActivityEvent[]> {
  return fetchAllPages<PublicActivityEvent>(
    `/v1/projects/${encodeURIComponent(projectId)}/activity`,
    "failed to load activity",
  );
}

export async function fetchProjectRepos(projectId: string): Promise<PublicRepo[] | null> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/repos?limit=100`);
  if (res.status === 404) {
    return null;
  }
  if (!res.ok) {
    throw await readApiError(res, "failed to load repositories");
  }
  const first = await parseJson<PageBody<PublicRepo>>(res);
  const items = [...(first.items ?? [])];
  let cursor = first.next_cursor;
  while (cursor) {
    const next = await apiFetch(
      withPageParams(`/v1/projects/${encodeURIComponent(projectId)}/repos`, cursor),
    );
    if (!next.ok) {
      throw await readApiError(next, "failed to load repositories");
    }
    const body = await parseJson<PageBody<PublicRepo>>(next);
    items.push(...(body.items ?? []));
    cursor = body.next_cursor;
  }
  return items;
}

export async function fetchRepo(repoId: string): Promise<PublicRepo | null> {
  const res = await apiFetch(`/v1/repos/${encodeURIComponent(repoId)}`);
  if (res.status === 404) {
    return null;
  }
  if (!res.ok) {
    throw await readApiError(res, "failed to load repository");
  }
  return parseJson<PublicRepo>(res);
}

export async function updateRepoIndexMode(
  repoId: string,
  indexMode: IndexMode,
): Promise<PublicRepo> {
  const res = await apiFetch(`/v1/repos/${encodeURIComponent(repoId)}`, {
    method: "PATCH",
    body: JSON.stringify({ index_mode: indexMode }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update repository");
  }
  return parseJson<PublicRepo>(res);
}

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

export type ActorRef = {
  type: string;
  id: string;
  display: string;
};

export type ContextSectionId =
  | "goals"
  | "non_goals"
  | "architecture"
  | "conventions"
  | "glossary"
  | "ownership"
  | "pitfalls"
  | "commands"
  | "stack"
  | "security"
  | "style"
  | "definition_of_done"
  | "custom";

export type ContextSection = {
  id: ContextSectionId;
  key?: string;
  title: string;
  body_md: string;
  ordinal: number;
};

export type ContextNode = {
  id: string;
  project_id: string;
  repo_id: string | null;
  task_id: string | null;
  scope_type: "project" | "repo" | "path" | "task";
  path: string;
  sections: ContextSection[];
  source: string;
  source_path: string | null;
  review_state: "reviewed" | "needs_review";
  updated_at: string;
  updated_by: ActorRef;
};

export type ContextRevisionSummary = {
  id: string;
  project_id: string;
  compiled_hash: string;
  compiler_version: string;
  target: { repo_id: string | null; path: string; task_id: string | null };
  token_estimate: number;
  source_node_ids: string[];
  session_id: string | null;
  created_at: string;
};

export type SessionBrief = {
  schema_version: string;
  compiler_version: string;
  project: { id: string; name: string; slug: string };
  compiled_at: string;
  revision_id: string;
  compiled_hash: string;
  target: { repo_id: string | null; path: string; task_id: string | null };
  milestone: { id: string; title: string; status: string } | null;
  task: { id: string; title: string } | null;
  sections: ContextSection[];
  constraints: { id: string; kind: string; body: string; scope_path: string; status: string }[];
  decisions_relevant: { id: string; title: string; status: string; decision: string }[];
  budget: {
    requested: number;
    used_estimate: number;
    tokenizer: string;
    overflow: boolean;
    dropped: string[];
  };
  sources: { node_id: string; scope_type: string; path: string }[];
};

export type ContextRevision = ContextRevisionSummary & {
  brief_markdown: string;
  brief: SessionBrief;
};

export type ContextImportResult = {
  nodes: ContextNode[];
  code_owners_written: number;
};

export async function fetchContextNodes(projectId: string): Promise<ContextNode[]> {
  return fetchAllPages<ContextNode>(
    `/v1/projects/${encodeURIComponent(projectId)}/context/nodes`,
    "failed to load context",
  );
}

export async function putContextNode(
  projectId: string,
  nodeId: string,
  body: Record<string, unknown>,
): Promise<ContextNode> {
  const res = await apiFetch(
    `/v1/projects/${encodeURIComponent(projectId)}/context/nodes/${encodeURIComponent(nodeId)}`,
    { method: "PUT", body: JSON.stringify(body) },
  );
  if (!res.ok) {
    throw await readApiError(res, "failed to save context");
  }
  return parseJson<ContextNode>(res);
}

export async function createContextNode(
  projectId: string,
  body: Record<string, unknown>,
): Promise<ContextNode> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/context/nodes`, {
    method: "POST",
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to save context");
  }
  return parseJson<ContextNode>(res);
}

export async function importContextFiles(
  projectId: string,
  files: { path: string; content: string }[],
): Promise<ContextImportResult> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/context/import`, {
    method: "POST",
    body: JSON.stringify({ files }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to import files");
  }
  return parseJson<ContextImportResult>(res);
}

export async function exportAgentsMd(projectId: string): Promise<string> {
  const res = await apiFetch(
    `/v1/projects/${encodeURIComponent(projectId)}/context/export/agents-md`,
  );
  if (!res.ok) {
    throw await readApiError(res, "failed to export");
  }
  return res.text();
}

export async function compileContext(
  projectId: string,
  input: { task_id?: string; path?: string; repo_id?: string } = {},
): Promise<SessionBrief> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/context/compile`, {
    method: "POST",
    body: JSON.stringify({
      project_id: projectId,
      ...input,
    }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to compile preview");
  }
  return parseJson<SessionBrief>(res);
}

export async function fetchContextRevisions(projectId: string): Promise<ContextRevisionSummary[]> {
  return fetchAllPages<ContextRevisionSummary>(
    `/v1/projects/${encodeURIComponent(projectId)}/context/revisions`,
    "failed to load revisions",
  );
}

export async function fetchContextRevision(
  projectId: string,
  revisionId: string,
): Promise<ContextRevision> {
  const res = await apiFetch(
    `/v1/projects/${encodeURIComponent(projectId)}/context/revisions/${encodeURIComponent(revisionId)}`,
  );
  if (!res.ok) {
    throw await readApiError(res, "failed to load revision");
  }
  return parseJson<ContextRevision>(res);
}

export async function createProjectRepo(
  projectId: string,
  input: {
    provider: "github" | "local";
    index_mode: IndexMode;
    local_root_hint?: string;
  },
): Promise<PublicRepo> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/repos`, {
    method: "POST",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to connect code");
  }
  return parseJson<PublicRepo>(res);
}

export async function mintProjectToken(projectId: string, name: string): Promise<PublicToken> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/tokens`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to mint token");
  }
  return parseJson<PublicToken>(res);
}

export async function requestRepoDetect(repoId: string): Promise<"started" | "later"> {
  const res = await apiFetch(`/v1/repos/${encodeURIComponent(repoId)}/detect`, {
    method: "POST",
    body: JSON.stringify({}),
  });
  if (res.ok || res.status === 202) {
    return "started";
  }
  if (res.status === 404 || res.status === 405 || res.status === 501) {
    return "later";
  }
  throw await readApiError(res, "failed to start detect");
}

export async function saveProjectBrief(
  projectId: string,
  sections: ContextSection[],
): Promise<ContextNode> {
  return createContextNode(projectId, { sections, scope_type: "project", path: "" });
}

export { fetchProjectMilestones, fetchProjectTasks } from "./roadmap";

import {
  createMilestone as createRoadmapMilestone,
  createTask as createRoadmapTask,
} from "./roadmap";
import type { PublicMilestone, PublicTask } from "./roadmap";

export async function createMilestone(
  projectId: string,
  input: { title: string; description?: string },
): Promise<PublicMilestone> {
  return createRoadmapMilestone(projectId, input.title, input.description ?? "");
}

export async function createTask(
  projectId: string,
  input: {
    title: string;
    description?: string;
    milestone_id?: string | null;
    label_ids?: string[];
  },
): Promise<PublicTask> {
  return createRoadmapTask(projectId, input, newIdempotencyKey());
}
