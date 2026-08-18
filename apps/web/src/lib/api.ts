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

export async function fetchAllPages<T>(
  load: (cursor: string | null) => Promise<Page<T>>,
): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  const seen = new Set<string>();
  for (;;) {
    const page = await load(cursor);
    items.push(...page.items);
    const next = page.next_cursor;
    if (!next || seen.has(next)) {
      return items;
    }
    seen.add(next);
    cursor = next;
  }
}

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

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
