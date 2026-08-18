import { apiRequest, type HttpRequest } from "./http.js";
import { codeIndexUnavailable, repoAmbiguous } from "./errors.js";
import type { InvokeContext } from "./types.js";
import type {
  GetChangedScopeArgs,
  GetFileArgs,
  GetOwnersArgs,
  GetRelatedFilesArgs,
  GetSymbolArgs,
  GetTreeArgs,
  SearchCodeArgs,
} from "./invoke-types.js";

export type CodeSource = {
  getTree(args: GetTreeArgs, ctx: InvokeContext): Promise<unknown>;
  searchCode(args: SearchCodeArgs, ctx: InvokeContext): Promise<unknown>;
  getFile(args: GetFileArgs, ctx: InvokeContext): Promise<unknown>;
  getSymbol(args: GetSymbolArgs, ctx: InvokeContext): Promise<unknown>;
  getOwners(args: GetOwnersArgs, ctx: InvokeContext): Promise<unknown>;
  getRelatedFiles(args: GetRelatedFilesArgs, ctx: InvokeContext): Promise<unknown>;
  getChangedScope(args: GetChangedScopeArgs, ctx: InvokeContext): Promise<unknown>;
};

function resolveRepoId(repoId: string | undefined, ctx: InvokeContext): string {
  const resolved = repoId ?? ctx.defaultRepoId;
  if (!resolved) {
    throw repoAmbiguous();
  }
  return resolved;
}

async function codeRequest(ctx: InvokeContext, request: HttpRequest): Promise<unknown> {
  try {
    return await apiRequest(ctx, request, { missingIndex: true });
  } catch (error) {
    if (
      error instanceof Error &&
      "code" in error &&
      (error as { code: string }).code === "not_found"
    ) {
      throw codeIndexUnavailable();
    }
    throw error;
  }
}

export function createHttpCodeSource(): CodeSource {
  return {
    getTree(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/tree`,
        query: {
          path: args.path,
          depth: args.depth ?? 2,
        },
      });
    },
    searchCode(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/search`,
        query: {
          q: args.q,
          mode: args.mode,
          lang: args.lang,
          path_prefix: args.path_prefix,
          limit: args.limit,
        },
      });
    },
    getFile(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/files`,
        query: {
          path: args.path,
          start_line: args.start_line,
          end_line: args.end_line,
        },
      });
    },
    getSymbol(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/symbols`,
        query: {
          name: args.name,
          path: args.path,
          kind: args.kind,
        },
      });
    },
    getOwners(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/owners`,
        query: { path: args.path },
      });
    },
    getRelatedFiles(args, ctx) {
      const repoId = resolveRepoId(args.repo_id, ctx);
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${repoId}/related`,
        query: {
          path: args.path,
          limit: args.limit,
        },
      });
    },
    getChangedScope(args, ctx) {
      return codeRequest(ctx, {
        method: "GET",
        path: `/v1/tasks/${args.task_id}/changed-scope`,
        query: { limit: args.limit },
      });
    },
  };
}
