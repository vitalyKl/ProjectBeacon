import type { ChangedScope, TreeCapsule } from "@beacon/api-spec";
import { extractIdentifiers } from "@beacon/index-core";

import type { AuthConfig } from "../auth/config.js";
import type { AuthStore } from "../auth/store.js";
import type { ProjectRepoRecord } from "../context/types.js";
import type { TaskRecord } from "../roadmap/types.js";

export const SIDECAR_SEEN_MS = 60_000;

export class CodeGatewayError extends Error {
  override readonly name = "CodeGatewayError";
  readonly status: number;
  readonly code: string;
  readonly messageText: string;
  readonly details: Record<string, unknown>;

  constructor(init: {
    status: number;
    code: string;
    message: string;
    details?: Record<string, unknown>;
  }) {
    super(init.message);
    this.status = init.status;
    this.code = init.code;
    this.messageText = init.message;
    this.details = init.details ?? {};
  }
}

export type CodeQuery =
  | { kind: "tree"; path?: string; depth?: number }
  | {
      kind: "search";
      q: string;
      mode?: string;
      lang?: string;
      pathPrefix?: string;
      limit?: number;
    }
  | { kind: "file"; path: string; startLine?: number; endLine?: number }
  | { kind: "symbol"; name: string; path?: string; symbolKind?: string }
  | { kind: "owners"; path: string }
  | { kind: "related"; path: string; limit?: number }
  | {
      kind: "changed_scope";
      identifiers?: string[];
      linkedPaths?: string[];
      pathPrefixes?: string[];
      limit?: number;
    };

export type CodeGateway = {
  query(repo: ProjectRepoRecord, query: CodeQuery): Promise<unknown>;
  health(repo: ProjectRepoRecord): Promise<boolean>;
};

type RpcQuery = Record<string, string | number | Array<string | undefined> | undefined>;

type IndexRpcClient = {
  query(repoId: string, path: string, query?: RpcQuery): Promise<unknown>;
  health(): Promise<boolean>;
};

function applyQuery(url: URL, query: RpcQuery | undefined): void {
  if (!query) {
    return;
  }
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === "") {
      continue;
    }
    if (Array.isArray(value)) {
      for (const item of value) {
        if (item !== undefined && item !== "") {
          url.searchParams.append(key, String(item));
        }
      }
      continue;
    }
    url.searchParams.set(key, String(value));
  }
}

export function createIndexRpcClient(options: {
  baseUrl: string;
  token?: string;
  fetchImpl?: typeof fetch;
}): IndexRpcClient {
  const fetchImpl = options.fetchImpl ?? fetch;
  const base = options.baseUrl.replace(/\/+$/, "");

  async function requestRaw(path: string, query?: RpcQuery): Promise<Response> {
    const url = new URL(`${base}${path.startsWith("/") ? path : `/${path}`}`);
    applyQuery(url, query);
    const headers: Record<string, string> = { accept: "application/json" };
    if (options.token) {
      headers.authorization = `Bearer ${options.token}`;
    }
    return fetchImpl(url, { method: "GET", headers });
  }

  return {
    async health() {
      try {
        const response = await requestRaw("/health");
        return response.ok;
      } catch {
        return false;
      }
    },
    async query(repoId, path, query) {
      let response: Response;
      try {
        response = await requestRaw(`/repos/${repoId}${path}`, query);
      } catch {
        throw new CodeGatewayError({
          status: 503,
          code: "code_index_unavailable",
          message: "code index unavailable",
        });
      }
      const text = await response.text();
      let parsed: unknown;
      if (text.length > 0) {
        try {
          parsed = JSON.parse(text) as unknown;
        } catch {
          parsed = undefined;
        }
      }
      if (!response.ok) {
        const err =
          parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
            ? (
                parsed as {
                  error?: { code?: string; message?: string; details?: Record<string, unknown> };
                }
              ).error
            : undefined;
        throw new CodeGatewayError({
          status: response.status,
          code: err?.code ?? (response.status === 503 ? "code_index_unavailable" : "unauthorized"),
          message: err?.message ?? "index request failed",
          details: err?.details ?? {},
        });
      }
      return parsed;
    },
  };
}

function usesWorkerIndex(repo: ProjectRepoRecord): boolean {
  return (
    repo.indexMode === "bind_mount" ||
    repo.indexMode === "hosted_clone" ||
    repo.indexMode === "both"
  );
}

type TunnelClient = {
  isLive(repoId: string): boolean;
  query(repoId: string, path: string, query?: RpcQuery): Promise<unknown>;
};

export function createCodeGateway(options: {
  config: Pick<AuthConfig, "indexRpcUrl" | "indexRpcToken">;
  store: Pick<AuthStore, "findSidecarConnectionByRepoId">;
  now: () => Date;
  fetchImpl?: typeof fetch;
  rpc?: IndexRpcClient;
  tunnel?: TunnelClient;
  tunnelEnabled?: () => boolean;
}): CodeGateway {
  const rpc =
    options.rpc ??
    createIndexRpcClient({
      baseUrl: options.config.indexRpcUrl,
      token: options.config.indexRpcToken,
      fetchImpl: options.fetchImpl,
    });

  function tunnelLive(repoId: string): boolean {
    return Boolean(options.tunnelEnabled?.() && options.tunnel?.isLive(repoId));
  }

  async function route(repo: ProjectRepoRecord): Promise<"tunnel" | "worker" | "none"> {
    if (repo.indexMode === "sidecar") {
      return tunnelLive(repo.id) ? "tunnel" : "none";
    }
    if (repo.indexMode === "both") {
      const sidecar = await options.store.findSidecarConnectionByRepoId(repo.id);
      if (
        sidecar &&
        options.now().getTime() - sidecar.lastSeenAt.getTime() <= SIDECAR_SEEN_MS &&
        tunnelLive(repo.id)
      ) {
        return "tunnel";
      }
    }
    if (usesWorkerIndex(repo)) {
      return "worker";
    }
    return "none";
  }

  async function dispatch(repo: ProjectRepoRecord, target: "tunnel" | "worker", query: CodeQuery) {
    const client = target === "tunnel" ? options.tunnel : rpc;
    if (!client) {
      throw new CodeGatewayError({
        status: 503,
        code: "code_index_unavailable",
        message: "code index unavailable",
      });
    }
    switch (query.kind) {
      case "tree":
        return client.query(repo.id, "/tree", { path: query.path, depth: query.depth });
      case "search":
        return client.query(repo.id, "/search", {
          q: query.q,
          mode: query.mode,
          lang: query.lang,
          path_prefix: query.pathPrefix,
          limit: query.limit,
        });
      case "file":
        return client.query(repo.id, "/files", {
          path: query.path,
          start_line: query.startLine,
          end_line: query.endLine,
        });
      case "symbol":
        return client.query(repo.id, "/symbols", {
          name: query.name,
          path: query.path,
          kind: query.symbolKind,
        });
      case "owners":
        return client.query(repo.id, "/owners", { path: query.path });
      case "related":
        return client.query(repo.id, "/related", { path: query.path, limit: query.limit });
      case "changed_scope":
        return client.query(repo.id, "/changed-scope", {
          limit: query.limit,
          identifier: query.identifiers,
          linked_path: query.linkedPaths,
          path_prefix: query.pathPrefixes,
        });
    }
  }

  return {
    async health(repo) {
      const target = await route(repo);
      if (target === "tunnel") {
        return true;
      }
      if (target !== "worker") {
        return false;
      }
      return rpc.health();
    },
    async query(repo, query) {
      const target = await route(repo);
      if (target === "none") {
        throw new CodeGatewayError({
          status: 503,
          code: "code_index_unavailable",
          message: "code index unavailable",
        });
      }
      return dispatch(repo, target, query);
    },
  };
}

export async function resolveRepoForCode(
  store: Pick<AuthStore, "findProjectRepoById" | "listProjectRepos" | "findProjectById">,
  projectId: string,
  requested: string | undefined,
): Promise<
  { ok: true; repo: ProjectRepoRecord } | { ok: false; reason: "not_found" | "ambiguous" }
> {
  if (requested) {
    const repo = await store.findProjectRepoById(requested);
    if (!repo || repo.projectId !== projectId) {
      return { ok: false, reason: "not_found" };
    }
    return { ok: true, repo };
  }
  const project = await store.findProjectById(projectId);
  if (project?.defaultRepoId) {
    const repo = await store.findProjectRepoById(project.defaultRepoId);
    if (repo && repo.projectId === projectId) {
      return { ok: true, repo };
    }
  }
  const repos = await store.listProjectRepos(projectId);
  if (repos.length === 1 && repos[0]) {
    return { ok: true, repo: repos[0] };
  }
  if (repos.length === 0) {
    return { ok: false, reason: "not_found" };
  }
  return { ok: false, reason: "ambiguous" };
}

export function taskChangedScopeQuery(task: TaskRecord, repoId: string, limit?: number): CodeQuery {
  const linked = task.linkedPaths
    .filter((item) => item.repo_id === repoId)
    .map((item) => item.path);
  return {
    kind: "changed_scope",
    identifiers: extractIdentifiers(task.title, task.description, task.agentBrief),
    linkedPaths: linked,
    pathPrefixes: linked.map((path) => path.replace(/\/[^/]+$/, "")).filter(Boolean),
    limit,
  };
}

export function presentTreeCapsule(repoId: string, payload: unknown): TreeCapsule | null {
  if (payload === null || typeof payload !== "object" || Array.isArray(payload)) {
    return null;
  }
  const items = (payload as { items?: unknown }).items;
  if (!Array.isArray(items)) {
    return null;
  }
  const entries: TreeCapsule["entries"] = [];
  for (const item of items) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      continue;
    }
    const record = item as Record<string, unknown>;
    if (typeof record["path"] !== "string") {
      continue;
    }
    entries.push({
      path: record["path"],
      kind: "dir",
      file_count: typeof record["file_count"] === "number" ? record["file_count"] : undefined,
      langs:
        record["langs"] !== null &&
        typeof record["langs"] === "object" &&
        !Array.isArray(record["langs"])
          ? (record["langs"] as Record<string, number>)
          : undefined,
    });
    const children = record["children"];
    if (Array.isArray(children)) {
      for (const child of children) {
        if (typeof child === "string") {
          entries.push({ path: child, kind: child.includes(".") ? "file" : "dir" });
        }
      }
    }
  }
  return { repo_id: repoId, root: ".", entries };
}

export function presentChangedScope(repoId: string, payload: unknown): ChangedScope | null {
  if (payload === null || typeof payload !== "object" || Array.isArray(payload)) {
    return null;
  }
  const record = payload as { paths?: unknown; reasons?: unknown };
  if (!Array.isArray(record.paths)) {
    return null;
  }
  const paths = record.paths
    .filter((item): item is string => typeof item === "string")
    .map((path) => ({ repo_id: repoId, path }));
  const reasons = Array.isArray(record.reasons)
    ? record.reasons.flatMap((item) => {
        if (item === null || typeof item !== "object" || Array.isArray(item)) {
          return [];
        }
        const row = item as { path?: unknown; reasons?: unknown };
        if (typeof row.path !== "string" || !Array.isArray(row.reasons)) {
          return [];
        }
        return row.reasons
          .filter((reason): reason is string => typeof reason === "string")
          .map((reason) => ({ path: row.path as string, repo_id: repoId, reason }));
      })
    : [];
  return { paths, reasons };
}
