import fs from "node:fs";
import path from "node:path";

import {
  IndexCore,
  PathEscapeError,
  extractIdentifiers,
  ownersForPath,
  readFileExcerpt,
  type IndexCore as IndexCoreType,
} from "@beacon/index-core";
import {
  codeIndexUnavailable,
  repoAmbiguous,
  toolError,
  type CodeSource,
  type InvokeContext,
} from "@beacon/mcp-tools";

import { apiGet, asProjectSnapshot } from "./api.js";

export type LocalIndexRegistry = {
  get(repoId: string): IndexCoreType | undefined;
  resolveRepoRoot(repoId: string): string | undefined;
};

export type SidecarRepoMap = {
  defaultRepoId?: string;
  roots: Record<string, string>;
};

function asRepoList(value: unknown): { id: string; local_root_hint?: string | null }[] {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return [];
  }
  const items = (value as { items?: unknown }).items;
  if (!Array.isArray(items)) {
    return [];
  }
  return items.flatMap((item) => {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return [];
    }
    const record = item as { id?: unknown; local_root_hint?: unknown };
    if (typeof record.id !== "string") {
      return [];
    }
    return [
      {
        id: record.id,
        local_root_hint: typeof record.local_root_hint === "string" ? record.local_root_hint : null,
      },
    ];
  });
}

export async function loadProjectRepos(
  ctx: InvokeContext,
): Promise<{ defaultRepoId?: string; repos: { id: string; local_root_hint?: string | null }[] }> {
  if (!ctx.projectId) {
    return { repos: [] };
  }
  const fetchImpl = ctx.fetch ?? fetch;
  const listed = await apiGet(
    ctx.baseUrl,
    ctx.token,
    `/v1/projects/${ctx.projectId}/repos`,
    fetchImpl,
  );
  const project = await apiGet(ctx.baseUrl, ctx.token, `/v1/projects/${ctx.projectId}`, fetchImpl);
  const snapshot = asProjectSnapshot(project.body);
  return {
    defaultRepoId:
      snapshot && "default_repo_id" in (project.body as object)
        ? typeof (project.body as { default_repo_id?: unknown }).default_repo_id === "string"
          ? (project.body as { default_repo_id: string }).default_repo_id
          : undefined
        : undefined,
    repos: listed.status >= 200 && listed.status < 300 ? asRepoList(listed.body) : [],
  };
}

export function resolveLocalRepoId(
  requested: string | undefined,
  ctx: InvokeContext,
  map: SidecarRepoMap,
): string {
  const resolved = requested ?? ctx.defaultRepoId ?? map.defaultRepoId;
  if (!resolved) {
    throw repoAmbiguous();
  }
  return resolved;
}

async function defaultRepoIdFromApi(
  ctx: InvokeContext,
  map: SidecarRepoMap,
): Promise<string | undefined> {
  if (ctx.defaultRepoId) {
    return ctx.defaultRepoId;
  }
  if (map.defaultRepoId) {
    return map.defaultRepoId;
  }
  if (!ctx.projectId) {
    return undefined;
  }
  const fetchImpl = ctx.fetch ?? fetch;
  const project = await apiGet(ctx.baseUrl, ctx.token, `/v1/projects/${ctx.projectId}`, fetchImpl);
  if (
    project.status < 200 ||
    project.status >= 300 ||
    project.body === null ||
    typeof project.body !== "object"
  ) {
    return undefined;
  }
  const value = (project.body as { default_repo_id?: unknown }).default_repo_id;
  return typeof value === "string" ? value : undefined;
}

export function createLocalCodeSource(options: {
  cwd: string;
  home: string;
  map: SidecarRepoMap;
}): CodeSource {
  const cores = new Map<string, IndexCoreType>();

  const open = (repoId: string): IndexCoreType => {
    const existing = cores.get(repoId);
    if (existing) {
      return existing;
    }
    const root = options.map.roots[repoId] ?? options.cwd;
    const dbPath = path.join(options.home, "index", `${repoId}.sqlite`);
    const core = new IndexCore({ repoRoot: root, dbPath });
    core.index();
    cores.set(repoId, core);
    return core;
  };

  const repoIdOf = async (requested: string | undefined, ctx: InvokeContext): Promise<string> => {
    return resolveLocalRepoId(
      requested,
      {
        ...ctx,
        defaultRepoId: ctx.defaultRepoId ?? (await defaultRepoIdFromApi(ctx, options.map)),
      },
      options.map,
    );
  };

  const withCore = <T>(repoId: string, fn: (core: IndexCoreType) => T): T => {
    try {
      return fn(open(repoId));
    } catch (error) {
      if (error instanceof PathEscapeError) {
        throw toolError("not_found", 404, "path not found");
      }
      throw error;
    }
  };

  return {
    async getTree(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) => ({
        items: core.getTree({ root: args.path ?? ".", depth: args.depth ?? 2 }).map((dir) => ({
          path: dir.path,
          file_count: dir.fileCount,
          byte_size: dir.byteSize,
          langs: dir.langs,
          important: dir.important,
          children: dir.children,
        })),
      }));
    },
    async searchCode(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) =>
        core.search({
          q: args.q,
          mode: args.mode,
          lang: args.lang,
          pathPrefix: args.path_prefix,
          limit: args.limit,
        }),
      );
    },
    async getFile(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) => {
        let indexed = null;
        try {
          indexed = core.readIndexedFileMetadata(args.path);
        } catch (error) {
          if (!(error instanceof PathEscapeError)) {
            throw error;
          }
        }
        const excerpt = readFileExcerpt(core.repoRoot, args.path, {
          startLine: args.start_line,
          endLine: args.end_line,
          indexed,
        });
        if (!excerpt.ok) {
          if (excerpt.reason === "binary") {
            throw toolError("unsupported_media", 415, "binary file");
          }
          throw toolError("not_found", 404, "file not found");
        }
        return {
          path: excerpt.path,
          start_line: excerpt.startLine,
          end_line: excerpt.endLine,
          content: excerpt.content,
          lang: excerpt.lang,
          bytes: excerpt.bytes,
        };
      });
    },
    async getSymbol(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) => {
        const found = core.getSymbol({ name: args.name, path: args.path, kind: args.kind });
        if (!found) {
          throw toolError("not_found", 404, "symbol not found");
        }
        return {
          id: found.id,
          path: found.path,
          name: found.name,
          kind: found.kind,
          start_line: found.startLine,
          end_line: found.endLine,
          parent_name: found.parentName,
        };
      });
    },
    async getOwners(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) => ({
        path: args.path,
        owners: ownersForPath(core.repoRoot, args.path),
      }));
    },
    async getRelatedFiles(args, ctx) {
      const repoId = await repoIdOf(args.repo_id, ctx);
      return withCore(repoId, (core) => ({
        items: core
          .getRelatedFiles({ path: args.path })
          .slice(0, args.limit ?? 50)
          .map((edge) => ({
            from_path: edge.fromPath,
            to_spec: edge.toSpec,
            to_path: edge.toPath,
          })),
      }));
    },
    async getChangedScope(args, ctx) {
      const fetchImpl = ctx.fetch ?? fetch;
      const taskRes = await apiGet(ctx.baseUrl, ctx.token, `/v1/tasks/${args.task_id}`, fetchImpl);
      if (taskRes.status === 404) {
        throw toolError("not_found", 404, "task not found");
      }
      if (taskRes.status < 200 || taskRes.status >= 300) {
        throw codeIndexUnavailable();
      }
      const task = taskRes.body as {
        title?: string;
        description?: string;
        agent_brief?: string;
        linked_paths?: { repo_id?: string; path?: string }[];
        project_id?: string;
      };
      const linked = (task.linked_paths ?? []).filter(
        (item): item is { repo_id: string; path: string } =>
          typeof item?.repo_id === "string" && typeof item.path === "string",
      );
      const repoId = await repoIdOf(linked[0]?.repo_id, ctx);
      return withCore(repoId, (core) =>
        core.getChangedScope({
          identifiers: extractIdentifiers(task.title, task.description, task.agent_brief),
          linkedPaths: linked.filter((item) => item.repo_id === repoId).map((item) => item.path),
          pathPrefixes: linked
            .filter((item) => item.repo_id === repoId)
            .map((item) => item.path.replace(/\/[^/]+$/, ""))
            .filter(Boolean),
          cap: args.limit ?? 50,
        }),
      );
    },
  };
}

export function ensureDir(dir: string): void {
  fs.mkdirSync(dir, { recursive: true });
}
