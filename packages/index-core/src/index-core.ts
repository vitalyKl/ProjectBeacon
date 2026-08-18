import { Indexer } from "./indexer.js";
import { ownersForPath } from "./owners.js";
import { toRepoPosixPath } from "./paths.js";
import {
  getChangedScope,
  getRelatedFiles,
  getSymbol,
  getTree,
  lastIndexedAtMs,
  readIndexedFileMetadata,
  searchContent,
  searchPaths,
  searchSymbols,
} from "./queries.js";
import type {
  ChangedScopeResult,
  ContentHit,
  DirCapsule,
  FileRecord,
  GetChangedScopeOptions,
  GetRelatedFilesOptions,
  GetSymbolOptions,
  GetTreeOptions,
  ImportEdge,
  IndexCoreOptions,
  IndexStats,
  SearchCodeOptions,
  SearchCodeResult,
  SearchContentOptions,
  SearchSymbolsOptions,
  SymbolRecord,
  TreeSitterParser,
} from "./types.js";

export class IndexCore {
  private readonly indexer: Indexer;

  constructor(options: IndexCoreOptions, parser?: TreeSitterParser) {
    this.indexer = new Indexer(options, parser);
  }

  get repoRoot(): string {
    return this.indexer.repoRoot;
  }

  get dbPath(): string {
    return this.indexer.dbPath;
  }

  close(): void {
    this.indexer.close();
  }

  index(): IndexStats {
    return this.indexer.index();
  }

  rebuild(): IndexStats {
    return this.indexer.rebuild();
  }

  getTree(options: GetTreeOptions = {}): DirCapsule[] {
    return getTree(this.indexer.db, this.repoRoot, options);
  }

  searchSymbols(options: SearchSymbolsOptions): SymbolRecord[] {
    return searchSymbols(this.indexer.db, options);
  }

  searchContent(options: SearchContentOptions): ContentHit[] {
    return searchContent(this.indexer.db, this.repoRoot, options);
  }

  lastIndexedAt(): Date | null {
    const ms = lastIndexedAtMs(this.indexer.db);
    return ms === null ? null : new Date(ms);
  }

  getOwners(filePath: string): string[] {
    return ownersForPath(this.repoRoot, toRepoPosixPath(this.repoRoot, filePath));
  }

  search(options: SearchCodeOptions): SearchCodeResult {
    const limit = options.limit ?? 50;
    const mode = options.mode ?? "auto";
    const pathPrefix = options.pathPrefix
      ? toRepoPosixPath(this.repoRoot, options.pathPrefix)
      : undefined;
    if (mode === "symbol" || (mode === "auto" && looksLikeSymbol(options.q))) {
      const symbols = searchSymbols(this.indexer.db, {
        q: options.q,
        prefix: true,
        limit,
      }).filter((row) => matchesSearchFilters(row.path, options.lang, pathPrefix));
      return {
        mode: mode === "auto" ? "symbol" : mode,
        items: symbols.map((symbol) => ({ kind: "symbol", symbol })),
      };
    }
    if (mode === "path") {
      const paths = searchPaths(this.indexer.db, {
        q: options.q,
        lang: options.lang,
        pathPrefix,
        limit,
      });
      return { mode, items: paths.map((row) => ({ kind: "path", path: row.path })) };
    }
    const hits = searchContent(this.indexer.db, this.repoRoot, {
      q: options.q,
      limit,
    }).filter((hit) => matchesSearchFilters(hit.path, options.lang, pathPrefix));
    return {
      mode: mode === "auto" ? "content" : mode,
      items: hits.map((hit) => ({ kind: "content", hit })),
    };
  }

  readIndexedFileMetadata(inputPath: string): FileRecord | null {
    return readIndexedFileMetadata(this.indexer.db, toRepoPosixPath(this.repoRoot, inputPath));
  }

  getSymbol(options: GetSymbolOptions): SymbolRecord | null {
    const found = getSymbol(this.indexer.db, {
      name: options.name,
      path: options.path ? toRepoPosixPath(this.repoRoot, options.path) : undefined,
    });
    if (!found) {
      return null;
    }
    if (options.kind && found.kind !== options.kind) {
      return null;
    }
    return found;
  }

  getRelatedFiles(options: GetRelatedFilesOptions): ImportEdge[] {
    return getRelatedFiles(this.indexer.db, {
      path: toRepoPosixPath(this.repoRoot, options.path),
    });
  }

  getChangedScope(options: GetChangedScopeOptions): ChangedScopeResult {
    return getChangedScope(this.indexer.db, {
      ...options,
      linkedPaths: options.linkedPaths?.map((item) => toRepoPosixPath(this.repoRoot, item)),
      pathPrefixes: options.pathPrefixes?.map((item) => toRepoPosixPath(this.repoRoot, item)),
    });
  }
}

function looksLikeSymbol(q: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(q.trim());
}

function matchesSearchFilters(
  filePath: string,
  lang: string | undefined,
  pathPrefix: string | undefined,
): boolean {
  if (
    pathPrefix &&
    filePath !== pathPrefix &&
    !filePath.startsWith(`${pathPrefix.replace(/\/$/, "")}/`) &&
    !filePath.startsWith(pathPrefix)
  ) {
    return false;
  }
  if (!lang) {
    return true;
  }
  return filePath.toLowerCase().endsWith(langExtension(lang));
}

function langExtension(lang: string): string {
  switch (lang) {
    case "typescript":
      return ".ts";
    case "tsx":
      return ".tsx";
    case "javascript":
      return ".js";
    case "python":
      return ".py";
    case "go":
      return ".go";
    case "rust":
      return ".rs";
    default:
      return `.${lang}`;
  }
}
