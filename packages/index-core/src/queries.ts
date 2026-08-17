import { escapeLikePrefix, ftsContentQuery, ftsPrefixToken } from "./fts.js";
import { parentDir, toRepoPosixPath } from "./paths.js";
import { searchRipgrep } from "./ripgrep.js";
import type { SqliteDb } from "./db.js";
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
  SearchContentOptions,
  SearchSymbolsOptions,
  SymbolRecord,
} from "./types.js";

interface FileRow {
  path: string;
  lang: string;
  size: number;
  mtime: number;
  sha256: string;
  blob_sha: string | null;
  is_binary: number;
  indexed_at: number;
}

interface DirRow {
  path: string;
  file_count: number;
  byte_size: number;
  langs_json: string | null;
  important_json: string | null;
}

interface SymbolRow {
  id: number;
  path: string;
  name: string;
  kind: string;
  start_line: number | null;
  end_line: number | null;
  parent_name: string | null;
}

function mapFile(row: FileRow): FileRecord {
  return {
    path: row.path,
    lang: row.lang as FileRecord["lang"],
    size: row.size,
    mtime: row.mtime,
    sha256: row.sha256,
    blobSha: row.blob_sha,
    isBinary: row.is_binary === 1,
    indexedAt: row.indexed_at,
  };
}

function mapSymbol(row: SymbolRow): SymbolRecord {
  return {
    id: row.id,
    path: row.path,
    name: row.name,
    kind: row.kind,
    startLine: row.start_line,
    endLine: row.end_line,
    parentName: row.parent_name,
  };
}

function mapDir(row: DirRow, children: string[]): DirCapsule {
  return {
    path: row.path,
    fileCount: row.file_count,
    byteSize: row.byte_size,
    langs: row.langs_json ? (JSON.parse(row.langs_json) as Record<string, number>) : {},
    important: row.important_json ? (JSON.parse(row.important_json) as string[]) : [],
    children,
  };
}

export function getTree(db: SqliteDb, repoRoot: string, options: GetTreeOptions = {}): DirCapsule[] {
  const depth = options.depth ?? 2;
  const root = options.root ? toRepoPosixPath(repoRoot, options.root) : ".";
  const dirs = db.prepare("SELECT path, file_count, byte_size, langs_json, important_json FROM dirs").all() as DirRow[];
  const byPath = new Map(dirs.map((dir) => [dir.path, dir]));
  const childrenByParent = new Map<string, string[]>();
  for (const dir of dirs) {
    const parent = parentDir(dir.path);
    if (!parent) {
      continue;
    }
    const list = childrenByParent.get(parent) ?? [];
    list.push(dir.path);
    childrenByParent.set(parent, list);
  }
  const files = db.prepare("SELECT path FROM files").all() as { path: string }[];
  for (const file of files) {
    const parent = parentDir(file.path) ?? ".";
    const list = childrenByParent.get(parent) ?? [];
    list.push(file.path);
    childrenByParent.set(parent, list);
  }
  for (const list of childrenByParent.values()) {
    list.sort((a, b) => a.localeCompare(b));
  }

  const result: DirCapsule[] = [];
  const visit = (dirPath: string, remaining: number): void => {
    const row = byPath.get(dirPath);
    if (!row) {
      return;
    }
    const children = childrenByParent.get(dirPath) ?? [];
    result.push(mapDir(row, remaining > 0 ? children : []));
    if (remaining <= 0) {
      return;
    }
    for (const child of children) {
      if (byPath.has(child)) {
        visit(child, remaining - 1);
      }
    }
  };
  visit(root, depth);
  return result;
}

export function searchSymbols(db: SqliteDb, options: SearchSymbolsOptions): SymbolRecord[] {
  const limit = options.limit ?? 50;
  const q = options.q.trim();
  if (!q) {
    return [];
  }
  if (options.prefix === false) {
    return (
      db
        .prepare(
          "SELECT id, path, name, kind, start_line, end_line, parent_name FROM symbols WHERE name = ? LIMIT ?",
        )
        .all(q, limit) as SymbolRow[]
    ).map(mapSymbol);
  }
  const fts = ftsPrefixToken(q);
  if (fts) {
    const ftsHits = db
      .prepare(
        `SELECT s.id, s.path, s.name, s.kind, s.start_line, s.end_line, s.parent_name
         FROM symbols_fts
         JOIN symbols s ON s.id = symbols_fts.rowid
         WHERE symbols_fts MATCH ?
         LIMIT ?`,
      )
      .all(fts, limit) as SymbolRow[];
    if (ftsHits.length > 0) {
      return ftsHits.map(mapSymbol);
    }
  }
  return (
    db
      .prepare(
        "SELECT id, path, name, kind, start_line, end_line, parent_name FROM symbols WHERE name LIKE ? ESCAPE '\\' LIMIT ?",
      )
      .all(`${escapeLikePrefix(q)}%`, limit) as SymbolRow[]
  ).map(mapSymbol);
}

export function searchContent(
  db: SqliteDb,
  repoRoot: string,
  options: SearchContentOptions,
): ContentHit[] {
  const limit = options.limit ?? 50;
  const q = options.q.trim();
  if (!q) {
    return [];
  }
  const fts = ftsContentQuery(q);
  const rows = db
    .prepare("SELECT path FROM files_fts WHERE files_fts MATCH ? LIMIT ?")
    .all(fts, limit) as { path: string }[];
  const hits: ContentHit[] = rows.map((row) => ({ path: row.path, source: "fts" }));
  if (options.useRipgrep === false || hits.length > 0) {
    return hits;
  }
  return searchRipgrep(repoRoot, q, limit) ?? hits;
}

export function readIndexedFileMetadata(db: SqliteDb, repoPosixPath: string): FileRecord | null {
  const row = db
    .prepare(
      "SELECT path, lang, size, mtime, sha256, blob_sha, is_binary, indexed_at FROM files WHERE path = ?",
    )
    .get(repoPosixPath) as FileRow | undefined;
  return row ? mapFile(row) : null;
}

export function getSymbol(db: SqliteDb, options: GetSymbolOptions): SymbolRecord | null {
  if (options.path) {
    const row = db
      .prepare(
        "SELECT id, path, name, kind, start_line, end_line, parent_name FROM symbols WHERE name = ? AND path = ? LIMIT 1",
      )
      .get(options.name, options.path) as SymbolRow | undefined;
    return row ? mapSymbol(row) : null;
  }
  const row = db
    .prepare(
      "SELECT id, path, name, kind, start_line, end_line, parent_name FROM symbols WHERE name = ? LIMIT 1",
    )
    .get(options.name) as SymbolRow | undefined;
  return row ? mapSymbol(row) : null;
}

export function getRelatedFiles(db: SqliteDb, options: GetRelatedFilesOptions): ImportEdge[] {
  const rows = db
    .prepare(
      `SELECT from_path, to_spec, to_path FROM imports
       WHERE from_path = ? OR to_path = ?`,
    )
    .all(options.path, options.path) as { from_path: string; to_spec: string; to_path: string | null }[];
  return rows.map((row) => ({
    fromPath: row.from_path,
    toSpec: row.to_spec,
    toPath: row.to_path,
  }));
}

export function getChangedScope(db: SqliteDb, options: GetChangedScopeOptions): ChangedScopeResult {
  const cap = options.cap ?? 50;
  const reasons = new Map<string, Set<string>>();
  const add = (filePath: string, reason: string): void => {
    const set = reasons.get(filePath) ?? new Set<string>();
    set.add(reason);
    reasons.set(filePath, set);
  };

  for (const linked of options.linkedPaths ?? []) {
    add(linked, "linked");
  }

  for (const prefix of options.pathPrefixes ?? []) {
    const rows = db
      .prepare("SELECT path FROM files WHERE path = ? OR path LIKE ? ESCAPE '\\' LIMIT ?")
      .all(prefix, `${escapeLikePrefix(prefix)}%`, cap) as { path: string }[];
    for (const row of rows) {
      add(row.path, `prefix:${prefix}`);
    }
  }

  for (const identifier of options.identifiers ?? []) {
    const token = identifier.trim();
    if (!token) {
      continue;
    }
    const fts = ftsPrefixToken(token);
    if (fts) {
      const symbolHits = db
        .prepare("SELECT path FROM symbols_fts WHERE symbols_fts MATCH ? LIMIT ?")
        .all(fts, cap) as { path: string }[];
      for (const hit of symbolHits) {
        add(hit.path, `symbol:${token}`);
      }
      const contentHits = db
        .prepare("SELECT path FROM files_fts WHERE files_fts MATCH ? LIMIT ?")
        .all(fts, cap) as { path: string }[];
      for (const hit of contentHits) {
        add(hit.path, `content:${token}`);
      }
    }
    const nameHits = db
      .prepare("SELECT path FROM symbols WHERE name = ? LIMIT ?")
      .all(token, cap) as { path: string }[];
    for (const hit of nameHits) {
      add(hit.path, `name:${token}`);
    }
  }

  const seed = [...reasons.keys()];
  for (const seedPath of seed) {
    const neighbors = getRelatedFiles(db, { path: seedPath });
    for (const edge of neighbors) {
      if (edge.fromPath !== seedPath) {
        add(edge.fromPath, `import:${seedPath}`);
      }
      if (edge.toPath && edge.toPath !== seedPath) {
        add(edge.toPath, `import:${seedPath}`);
      }
    }
  }

  const ranked = [...reasons.entries()]
    .sort((a, b) => b[1].size - a[1].size || a[0].localeCompare(b[0]))
    .slice(0, cap);

  return {
    paths: ranked.map(([filePath]) => filePath),
    reasons: ranked.map(([filePath, set]) => ({
      path: filePath,
      reasons: [...set],
    })),
  };
}
