import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { BINARY_PROBE_BYTES, containsNul, isDeniedDirName, isDeniedFile } from "./denylist.js";
import { openDatabase, resetDatabase, type SqliteDb } from "./db.js";
import { detectLanguage, isImportantFile } from "./languages.js";
import { basename, parentDir, toFsPath, toRepoPosixPath } from "./paths.js";
import { createParser } from "./parser.js";
import { collectPackageMaps, readPackageJson, resolveImport } from "./resolve.js";
import type {
  ExtractedImport,
  ExtractedSymbol,
  IndexCoreOptions,
  IndexStats,
  LanguageId,
  TreeSitterParser,
} from "./types.js";

interface FileState {
  path: string;
  size: number;
  mtime: number;
  sha256: string;
}

interface PendingFile {
  path: string;
  lang: LanguageId;
  size: number;
  mtime: number;
  sha256: string;
  isBinary: boolean;
  content: string | null;
  symbols: ExtractedSymbol[];
  imports: ExtractedImport[];
}

export class Indexer {
  readonly repoRoot: string;
  readonly dbPath: string;
  readonly db: SqliteDb;
  private readonly parser: TreeSitterParser;

  constructor(options: IndexCoreOptions, parser: TreeSitterParser = createParser()) {
    this.repoRoot = options.repoRoot;
    this.dbPath = options.dbPath;
    this.parser = parser;
    this.db = openDatabase(options.dbPath);
  }

  close(): void {
    this.db.close();
  }

  rebuild(): IndexStats {
    resetDatabase(this.db);
    return this.index();
  }

  index(): IndexStats {
    const started = Date.now();
    const stats: IndexStats = {
      scanned: 0,
      indexed: 0,
      skippedUnchanged: 0,
      skippedDenied: 0,
      skippedBinary: 0,
      removed: 0,
      elapsedMs: 0,
    };

    const existing = this.loadExisting();
    const seen = new Set<string>();
    const pending: PendingFile[] = [];
    const visitedDirs = new Set<string>();

    const walk = (absDir: string): void => {
      let realDir: string;
      try {
        realDir = fs.realpathSync(absDir);
      } catch {
        return;
      }
      if (visitedDirs.has(realDir)) {
        return;
      }
      visitedDirs.add(realDir);

      let entries: fs.Dirent[];
      try {
        entries = fs.readdirSync(absDir, { withFileTypes: true });
      } catch {
        return;
      }
      for (const entry of entries) {
        const absPath = path.join(absDir, entry.name);
        let st: fs.Stats;
        try {
          st = fs.statSync(absPath);
        } catch {
          continue;
        }
        if (st.isDirectory()) {
          if (isDeniedDirName(entry.name)) {
            stats.skippedDenied += 1;
            continue;
          }
          walk(absPath);
          continue;
        }
        if (!st.isFile()) {
          continue;
        }
        let repoPath: string;
        try {
          repoPath = toRepoPosixPath(this.repoRoot, absPath);
        } catch {
          continue;
        }
        if (isDeniedFile(repoPath)) {
          stats.skippedDenied += 1;
          continue;
        }
        stats.scanned += 1;
        seen.add(repoPath);
        const mtime = Math.trunc(st.mtimeMs);
        const prev = existing.get(repoPath);
        if (prev && prev.mtime === mtime && prev.size === st.size) {
          stats.skippedUnchanged += 1;
          continue;
        }
        const file = this.readFile(repoPath, absPath, st.size, mtime);
        if (prev && prev.sha256 === file.sha256) {
          this.db.prepare("UPDATE files SET mtime = ?, size = ?, indexed_at = ? WHERE path = ?").run(
            mtime,
            st.size,
            Date.now(),
            repoPath,
          );
          stats.skippedUnchanged += 1;
          continue;
        }
        pending.push(file);
      }
    };

    walk(path.resolve(this.repoRoot));

    const stale = [...existing.keys()].filter((filePath) => !seen.has(filePath));
    this.applyChanges(pending, stale);
    stats.indexed = pending.length;
    stats.skippedBinary = pending.filter((file) => file.isBinary).length;
    stats.removed = stale.length;
    stats.elapsedMs = Date.now() - started;
    this.db.prepare("INSERT INTO meta (k, v) VALUES ('last_indexed_at', ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v").run(
      String(Date.now()),
    );
    return stats;
  }

  private loadExisting(): Map<string, FileState> {
    const rows = this.db.prepare("SELECT path, size, mtime, sha256 FROM files").all() as FileState[];
    return new Map(rows.map((row) => [row.path, row]));
  }

  private readFile(repoPath: string, absPath: string, size: number, mtime: number): PendingFile {
    const fd = fs.openSync(absPath, "r");
    try {
      const probeSize = Math.min(size, BINARY_PROBE_BYTES);
      const probe = Buffer.alloc(probeSize);
      if (probeSize > 0) {
        fs.readSync(fd, probe, 0, probeSize, 0);
      }
      const isBinary = containsNul(probe);
      const hash = createHash("sha256");
      if (isBinary) {
        hash.update(probe);
        if (size > probeSize) {
          const rest = Buffer.alloc(64 * 1024);
          let offset = probeSize;
          while (offset < size) {
            const read = fs.readSync(fd, rest, 0, rest.length, offset);
            if (read <= 0) {
              break;
            }
            hash.update(rest.subarray(0, read));
            offset += read;
          }
        }
        return {
          path: repoPath,
          lang: detectLanguage(repoPath),
          size,
          mtime,
          sha256: hash.digest("hex"),
          isBinary: true,
          content: null,
          symbols: [],
          imports: [],
        };
      }
      const content = fs.readFileSync(absPath, "utf8");
      hash.update(content);
      const lang = detectLanguage(repoPath);
      const parsed = this.parser.parse(content, lang);
      return {
        path: repoPath,
        lang,
        size,
        mtime,
        sha256: hash.digest("hex"),
        isBinary: false,
        content,
        symbols: parsed.symbols,
        imports: parsed.imports,
      };
    } finally {
      fs.closeSync(fd);
    }
  }

  private applyChanges(pending: PendingFile[], stale: string[]): void {
    const deleteFile = this.db.prepare("DELETE FROM files WHERE path = ?");
    const selectSymbolIds = this.db.prepare("SELECT id FROM symbols WHERE path = ?");
    const deleteSymbols = this.db.prepare("DELETE FROM symbols WHERE path = ?");
    const deleteSymbolFts = this.db.prepare("DELETE FROM symbols_fts WHERE rowid = ?");
    const deleteImports = this.db.prepare("DELETE FROM imports WHERE from_path = ?");
    const deleteFileFts = this.db.prepare("DELETE FROM files_fts WHERE path = ?");
    const insertFile = this.db.prepare(
      `INSERT INTO files (path, lang, size, mtime, sha256, blob_sha, is_binary, indexed_at)
       VALUES (@path, @lang, @size, @mtime, @sha256, NULL, @is_binary, @indexed_at)
       ON CONFLICT(path) DO UPDATE SET
         lang = excluded.lang,
         size = excluded.size,
         mtime = excluded.mtime,
         sha256 = excluded.sha256,
         is_binary = excluded.is_binary,
         indexed_at = excluded.indexed_at`,
    );
    const insertSymbol = this.db.prepare(
      `INSERT INTO symbols (path, name, kind, start_line, end_line, parent_name)
       VALUES (?, ?, ?, ?, ?, ?)`,
    );
    const insertFileFts = this.db.prepare("INSERT INTO files_fts (path, content) VALUES (?, ?)");
    const insertSymbolFts = this.db.prepare(
      "INSERT INTO symbols_fts (rowid, name, path) VALUES (?, ?, ?)",
    );
    const insertImport = this.db.prepare(
      "INSERT INTO imports (from_path, to_spec, to_path) VALUES (?, ?, ?)",
    );

    const apply = this.db.transaction(() => {
      const purgeSymbols = (filePath: string): void => {
        const ids = selectSymbolIds.all(filePath) as { id: number }[];
        for (const row of ids) {
          deleteSymbolFts.run(row.id);
        }
        deleteSymbols.run(filePath);
      };
      for (const stalePath of stale) {
        deleteFile.run(stalePath);
        purgeSymbols(stalePath);
        deleteImports.run(stalePath);
        deleteFileFts.run(stalePath);
      }
      for (const file of pending) {
        purgeSymbols(file.path);
        deleteImports.run(file.path);
        deleteFileFts.run(file.path);
        insertFile.run({
          path: file.path,
          lang: file.lang,
          size: file.size,
          mtime: file.mtime,
          sha256: file.sha256,
          is_binary: file.isBinary ? 1 : 0,
          indexed_at: Date.now(),
        });
        if (!file.isBinary && file.content !== null) {
          insertFileFts.run(file.path, file.content);
        }
        for (const symbol of file.symbols) {
          const info = insertSymbol.run(
            file.path,
            symbol.name,
            symbol.kind,
            symbol.startLine,
            symbol.endLine,
            symbol.parentName,
          );
          insertSymbolFts.run(Number(info.lastInsertRowid), symbol.name, file.path);
        }
        for (const edge of file.imports) {
          insertImport.run(file.path, edge.toSpec, null);
        }
      }
    });
    apply();

    this.resolveImportsAndDirs();
  }

  private resolveImportsAndDirs(): void {
    const files = this.db.prepare("SELECT path, lang, size FROM files").all() as {
      path: string;
      lang: string;
      size: number;
    }[];
    const existing = new Set(files.map((file) => file.path));
    const packageMap = collectPackageMaps(existing, (pkgPath) =>
      readPackageJson(toFsPath(this.repoRoot, pkgPath)),
    );
    const edges = this.db.prepare("SELECT rowid, from_path, to_spec FROM imports").all() as {
      rowid: number;
      from_path: string;
      to_spec: string;
    }[];
    const updateImport = this.db.prepare("UPDATE imports SET to_path = ? WHERE rowid = ?");
    const deleteDirs = this.db.prepare("DELETE FROM dirs");
    const insertDir = this.db.prepare(
      "INSERT INTO dirs (path, file_count, byte_size, langs_json, important_json) VALUES (?, ?, ?, ?, ?)",
    );

    const rewrite = this.db.transaction(() => {
      for (const edge of edges) {
        updateImport.run(
          resolveImport(edge.from_path, edge.to_spec, existing, packageMap),
          edge.rowid,
        );
      }

      const dirStats = new Map<
        string,
        { fileCount: number; byteSize: number; langs: Record<string, number>; important: Set<string> }
      >();
      const ensure = (dirPath: string) => {
        let current = dirStats.get(dirPath);
        if (!current) {
          current = { fileCount: 0, byteSize: 0, langs: {}, important: new Set() };
          dirStats.set(dirPath, current);
        }
        return current;
      };
      ensure(".");
      for (const file of files) {
        let dir = parentDir(file.path) ?? ".";
        while (dir) {
          const stats = ensure(dir);
          stats.fileCount += 1;
          stats.byteSize += file.size;
          stats.langs[file.lang] = (stats.langs[file.lang] ?? 0) + 1;
          if (isImportantFile(file.path) && (parentDir(file.path) ?? ".") === dir) {
            stats.important.add(basename(file.path));
          }
          const next = parentDir(dir);
          if (next === null) {
            break;
          }
          dir = next;
        }
      }
      deleteDirs.run();
      for (const [dirPath, stats] of dirStats) {
        insertDir.run(
          dirPath,
          stats.fileCount,
          stats.byteSize,
          JSON.stringify(stats.langs),
          JSON.stringify([...stats.important].sort()),
        );
      }
    });
    rewrite();
  }
}
