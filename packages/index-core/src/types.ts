export type LanguageId =
  | "typescript"
  | "tsx"
  | "javascript"
  | "python"
  | "go"
  | "rust"
  | "java"
  | "csharp"
  | "json"
  | "yaml"
  | "markdown"
  | "unknown";

export interface FileRecord {
  path: string;
  lang: LanguageId;
  size: number;
  mtime: number;
  sha256: string;
  blobSha: string | null;
  isBinary: boolean;
  indexedAt: number;
}

export interface DirCapsule {
  path: string;
  fileCount: number;
  byteSize: number;
  langs: Record<string, number>;
  important: string[];
  children: string[];
}

export interface SymbolRecord {
  id: number;
  path: string;
  name: string;
  kind: string;
  startLine: number | null;
  endLine: number | null;
  parentName: string | null;
}

export interface ImportEdge {
  fromPath: string;
  toSpec: string;
  toPath: string | null;
}

export interface ExtractedSymbol {
  name: string;
  kind: string;
  startLine: number;
  endLine: number;
  parentName: string | null;
}

export interface ExtractedImport {
  toSpec: string;
}

export interface ParseResult {
  symbols: ExtractedSymbol[];
  imports: ExtractedImport[];
}

export interface TreeSitterParser {
  parse(source: string, lang: LanguageId): ParseResult;
}

export interface IndexWarning {
  path: string;
  kind: "bcn_token";
}

export interface IndexStats {
  scanned: number;
  indexed: number;
  skippedUnchanged: number;
  skippedDenied: number;
  skippedBinary: number;
  removed: number;
  warnings: IndexWarning[];
  elapsedMs: number;
}

export interface GetTreeOptions {
  root?: string;
  depth?: number;
}

export interface SearchSymbolsOptions {
  q: string;
  prefix?: boolean;
  limit?: number;
}

export interface SearchContentOptions {
  q: string;
  limit?: number;
  useRipgrep?: boolean;
}

export interface GetSymbolOptions {
  name: string;
  path?: string;
}

export interface GetRelatedFilesOptions {
  path: string;
}

export interface GetChangedScopeOptions {
  identifiers?: string[];
  linkedPaths?: string[];
  pathPrefixes?: string[];
  cap?: number;
}

export interface ChangedScopeReason {
  path: string;
  reasons: string[];
}

export interface ChangedScopeResult {
  paths: string[];
  reasons: ChangedScopeReason[];
}

export interface ContentHit {
  path: string;
  snippet?: string;
  source: "fts" | "ripgrep";
}

export interface IndexCoreOptions {
  repoRoot: string;
  dbPath: string;
}
