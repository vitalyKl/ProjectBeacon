import { Indexer } from "./indexer.js";
import { toRepoPosixPath } from "./paths.js";
import {
  getChangedScope,
  getRelatedFiles,
  getSymbol,
  getTree,
  readIndexedFileMetadata,
  searchContent,
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

  readIndexedFileMetadata(inputPath: string): FileRecord | null {
    return readIndexedFileMetadata(this.indexer.db, toRepoPosixPath(this.repoRoot, inputPath));
  }

  getSymbol(options: GetSymbolOptions): SymbolRecord | null {
    return getSymbol(this.indexer.db, {
      name: options.name,
      path: options.path ? toRepoPosixPath(this.repoRoot, options.path) : undefined,
    });
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

