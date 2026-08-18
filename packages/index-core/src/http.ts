import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";

import { PathEscapeError } from "./paths.js";
import { GET_FILE_MAX_LINES, readFileExcerpt } from "./file.js";
import { ownersForPath } from "./owners.js";
import type { IndexCore } from "./index-core.js";
import type { DirCapsule, ImportEdge, SymbolRecord } from "./types.js";

export const DEFAULT_INDEX_HTTP_PORT = 7744;
export const DEFAULT_INDEX_HTTP_HOST = "127.0.0.1";

export type IndexHttpAuth = {
  token: string;
};

export type IndexHttpOptions = {
  host?: string;
  port?: number;
  auth?: IndexHttpAuth;
  resolve: (repoId: string) => IndexCore | undefined | Promise<IndexCore | undefined>;
};

export type PresentedTree = {
  path: string;
  file_count: number;
  byte_size: number;
  langs: Record<string, number>;
  important: string[];
  children: string[];
};

export type PresentedSymbol = {
  id: number;
  path: string;
  name: string;
  kind: string;
  start_line: number | null;
  end_line: number | null;
  parent_name: string | null;
};

export type PresentedContentHit = {
  path: string;
  snippet?: string;
  source: "fts" | "ripgrep";
};

export type PresentedImportEdge = {
  from_path: string;
  to_spec: string;
  to_path: string | null;
};

export type PresentedFile = {
  path: string;
  start_line: number;
  end_line: number;
  content: string;
  lang: string;
  bytes: number;
};

function json(res: ServerResponse, status: number, body: unknown): void {
  res.statusCode = status;
  res.setHeader("content-type", "application/json");
  res.end(JSON.stringify(body));
}

function errorBody(code: string, message: string, details: Record<string, unknown> = {}) {
  return { error: { code, message, details } };
}

function parseUrl(req: IncomingMessage): URL {
  return new URL(req.url ?? "/", "http://127.0.0.1");
}

function first(value: string | null): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function parseIntParam(value: string | null, fallback?: number): number | undefined {
  if (value === null || value === "") {
    return fallback;
  }
  if (!/^-?[0-9]+$/.test(value)) {
    return undefined;
  }
  return Number(value);
}

function presentTree(dirs: DirCapsule[]): PresentedTree[] {
  return dirs.map((dir) => ({
    path: dir.path,
    file_count: dir.fileCount,
    byte_size: dir.byteSize,
    langs: dir.langs,
    important: dir.important,
    children: dir.children,
  }));
}

function presentSymbol(row: SymbolRecord): PresentedSymbol {
  return {
    id: row.id,
    path: row.path,
    name: row.name,
    kind: row.kind,
    start_line: row.startLine,
    end_line: row.endLine,
    parent_name: row.parentName,
  };
}

function presentEdge(edge: ImportEdge): PresentedImportEdge {
  return {
    from_path: edge.fromPath,
    to_spec: edge.toSpec,
    to_path: edge.toPath,
  };
}

function bearerToken(req: IncomingMessage): string | undefined {
  const header = req.headers.authorization;
  if (!header || !header.startsWith("Bearer ")) {
    return undefined;
  }
  const token = header.slice("Bearer ".length).trim();
  return token.length > 0 ? token : undefined;
}

function authorize(req: IncomingMessage, auth: IndexHttpAuth | undefined): boolean {
  if (!auth) {
    return true;
  }
  const provided = bearerToken(req);
  return Boolean(provided && provided === auth.token);
}

function repoIdFrom(pathname: string): string | undefined {
  const match = /^\/repos\/([^/]+)(?:\/|$)/.exec(pathname);
  return match?.[1];
}

export function handleIndexRequest(
  req: IncomingMessage,
  res: ServerResponse,
  options: Pick<IndexHttpOptions, "auth" | "resolve">,
): void {
  void handleIndexRequestAsync(req, res, options);
}

async function handleIndexRequestAsync(
  req: IncomingMessage,
  res: ServerResponse,
  options: Pick<IndexHttpOptions, "auth" | "resolve">,
): Promise<void> {
  if (req.method !== "GET") {
    json(res, 404, errorBody("not_found", "not found"));
    return;
  }
  if (!authorize(req, options.auth)) {
    json(res, 401, errorBody("unauthorized", "invalid token"));
    return;
  }

  const url = parseUrl(req);
  if (url.pathname === "/health") {
    json(res, 200, { status: "ok" });
    return;
  }

  const repoId = repoIdFrom(url.pathname);
  if (!repoId) {
    json(res, 404, errorBody("not_found", "not found"));
    return;
  }
  let core: IndexCore | undefined;
  try {
    core = await options.resolve(repoId);
  } catch {
    json(res, 503, errorBody("code_index_unavailable", "code index unavailable"));
    return;
  }
  if (!core) {
    json(res, 503, errorBody("code_index_unavailable", "code index unavailable"));
    return;
  }

  const rest = url.pathname.slice(`/repos/${repoId}`.length) || "/";
  try {
    if (rest === "/tree") {
      const depthRaw = parseIntParam(url.searchParams.get("depth"), 2);
      if (depthRaw === undefined || depthRaw < 1 || depthRaw > 4) {
        json(res, 400, errorBody("unauthorized", "invalid depth", { reason: "invalid_query" }));
        return;
      }
      const tree = core.getTree({
        root: first(url.searchParams.get("path")) ?? ".",
        depth: depthRaw,
      });
      json(res, 200, { items: presentTree(tree) });
      return;
    }

    if (rest === "/search") {
      const q = first(url.searchParams.get("q"));
      if (!q) {
        json(res, 400, errorBody("unauthorized", "q is required", { reason: "invalid_query" }));
        return;
      }
      const mode = first(url.searchParams.get("mode")) ?? "auto";
      if (mode !== "symbol" && mode !== "content" && mode !== "path" && mode !== "auto") {
        json(res, 400, errorBody("unauthorized", "invalid mode", { reason: "invalid_query" }));
        return;
      }
      const limit = parseIntParam(url.searchParams.get("limit"), 50);
      if (limit === undefined || limit < 1 || limit > 100) {
        json(res, 400, errorBody("unauthorized", "invalid limit", { reason: "invalid_query" }));
        return;
      }
      json(
        res,
        200,
        core.search({
          q,
          mode,
          lang: first(url.searchParams.get("lang")),
          pathPrefix: first(url.searchParams.get("path_prefix")),
          limit,
        }),
      );
      return;
    }

    if (rest === "/files") {
      const filePath = first(url.searchParams.get("path"));
      if (!filePath) {
        json(res, 400, errorBody("unauthorized", "path is required", { reason: "invalid_query" }));
        return;
      }
      const startLine = parseIntParam(url.searchParams.get("start_line"));
      const endLine = parseIntParam(url.searchParams.get("end_line"));
      if (
        (url.searchParams.has("start_line") && (startLine === undefined || startLine < 1)) ||
        (url.searchParams.has("end_line") && (endLine === undefined || endLine < 1))
      ) {
        json(
          res,
          400,
          errorBody("unauthorized", "invalid line range", { reason: "invalid_query" }),
        );
        return;
      }
      const excerpt = readFileExcerpt(core.repoRoot, filePath, {
        startLine,
        endLine,
        indexed: (() => {
          try {
            return core.readIndexedFileMetadata(filePath);
          } catch (error) {
            if (error instanceof PathEscapeError) {
              return null;
            }
            throw error;
          }
        })(),
      });
      if (!excerpt.ok) {
        if (excerpt.reason === "binary") {
          json(res, 415, errorBody("unsupported_media", "binary file"));
          return;
        }
        json(res, 404, errorBody("not_found", "file not found"));
        return;
      }
      const body: PresentedFile = {
        path: excerpt.path,
        start_line: excerpt.startLine,
        end_line: excerpt.endLine,
        content: excerpt.content,
        lang: excerpt.lang,
        bytes: excerpt.bytes,
      };
      json(res, 200, body);
      return;
    }

    if (rest === "/symbols") {
      const name = first(url.searchParams.get("name"));
      if (!name) {
        json(res, 400, errorBody("unauthorized", "name is required", { reason: "invalid_query" }));
        return;
      }
      const symbol = core.getSymbol({
        name,
        path: first(url.searchParams.get("path")),
      });
      if (!symbol) {
        json(res, 404, errorBody("not_found", "symbol not found"));
        return;
      }
      const kind = first(url.searchParams.get("kind"));
      if (kind && symbol.kind !== kind) {
        json(res, 404, errorBody("not_found", "symbol not found"));
        return;
      }
      json(res, 200, presentSymbol(symbol));
      return;
    }

    if (rest === "/owners") {
      const filePath = first(url.searchParams.get("path"));
      if (!filePath) {
        json(res, 400, errorBody("unauthorized", "path is required", { reason: "invalid_query" }));
        return;
      }
      const owners = ownersForPath(core.repoRoot, filePath);
      json(res, 200, { path: filePath, owners });
      return;
    }

    if (rest === "/related") {
      const filePath = first(url.searchParams.get("path"));
      if (!filePath) {
        json(res, 400, errorBody("unauthorized", "path is required", { reason: "invalid_query" }));
        return;
      }
      const limit = parseIntParam(url.searchParams.get("limit"), 50);
      if (limit === undefined || limit < 1 || limit > 100) {
        json(res, 400, errorBody("unauthorized", "invalid limit", { reason: "invalid_query" }));
        return;
      }
      const edges = core.getRelatedFiles({ path: filePath }).slice(0, limit);
      json(res, 200, { items: edges.map(presentEdge) });
      return;
    }

    if (rest === "/changed-scope") {
      const limit = parseIntParam(url.searchParams.get("limit"), 50);
      if (limit === undefined || limit < 1 || limit > 100) {
        json(res, 400, errorBody("unauthorized", "invalid limit", { reason: "invalid_query" }));
        return;
      }
      const identifiers = collectParams(url.searchParams, "identifier");
      const linkedPaths = collectParams(url.searchParams, "linked_path");
      const pathPrefixes = collectParams(url.searchParams, "path_prefix");
      const scope = core.getChangedScope({
        identifiers,
        linkedPaths,
        pathPrefixes,
        cap: limit,
      });
      json(res, 200, {
        paths: scope.paths,
        reasons: scope.reasons,
      });
      return;
    }
  } catch (error) {
    if (error instanceof PathEscapeError) {
      json(res, 404, errorBody("not_found", "path not found"));
      return;
    }
    throw error;
  }

  json(res, 404, errorBody("not_found", "not found"));
}

export function createIndexHttpServer(options: IndexHttpOptions): Server {
  return createServer((req, res) => {
    void handleIndexRequestAsync(req, res, options).catch(() => {
      if (!res.headersSent) {
        json(res, 500, errorBody("unauthorized", "internal error"));
      }
    });
  });
}

export function listenIndexHttp(
  options: IndexHttpOptions,
): Promise<{ server: Server; host: string; port: number }> {
  const host = options.host ?? DEFAULT_INDEX_HTTP_HOST;
  const port = options.port ?? DEFAULT_INDEX_HTTP_PORT;
  const server = createIndexHttpServer(options);
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => {
      server.off("error", reject);
      const address = server.address();
      const bound = address && typeof address === "object" ? address.port : port;
      resolve({ server, host, port: bound });
    });
  });
}

function collectParams(params: URLSearchParams, name: string): string[] {
  const values = [...params.getAll(name), ...params.getAll(`${name}[]`)];
  for (const [key, value] of params.entries()) {
    if (
      key === name ||
      key === `${name}[]` ||
      key.startsWith(`${name}_`) ||
      key.startsWith(`${name}[`)
    ) {
      values.push(value);
    }
  }
  return [...new Set(values.map((item) => item.trim()).filter(Boolean))];
}

export { GET_FILE_MAX_LINES };
