# @beacon/index-core

Library-only incremental code index: tree-sitter symbols, SQLite FTS5, POSIX paths.

## Usage

```ts
import { IndexCore } from "@beacon/index-core";

const index = new IndexCore({
  repoRoot: "/path/to/repo",
  dbPath: "/path/to/index.sqlite",
});
index.index();
index.getTree({ depth: 2 });
index.searchSymbols({ q: "gree", prefix: true });
index.searchContent({ q: "hello" });
index.getRelatedFiles({ path: "src/index.ts" });
index.getChangedScope({ identifiers: ["greet"], linkedPaths: ["src/index.ts"] });
index.close();
```

WAL is enabled. Callers should assume a single writer.

## Paths

SQLite stores POSIX paths. Windows inputs convert `\` → `/` and strip an in-repo drive prefix. Paths that escape the repo root throw `PathEscapeError`.

## Binary and denylist

If the first 8KB of a file contain a NUL byte, `files.is_binary=1` and content is not FTS-indexed. `.git`, `node_modules`, `dist`, `.next`, `.env`, `*.pem`, and similar secret/generated paths are skipped.

## Languages

v1 language ids: TypeScript/TSX, JavaScript, Python, Go, Rust, Java, C#, JSON, YAML, Markdown. Unknown languages still get a tree row and FTS content, but no symbols.

Official tree-sitter grammars are used for TypeScript/TSX, JavaScript, and Python. Other listed languages are stored with FTS content only until compatible grammars are wired through `TreeSitterParser`.

Import edges resolve literal relative specs, same-directory files, and `package.json` `exports`/`main`. Unresolved specs keep `to_path` NULL. No tsconfig path mapping.

## Bench vs SLOs

```bash
pnpm --filter @beacon/index-core bench          # 1k files (CI-safe)
BENCH_FILES=50000 pnpm --filter @beacon/index-core bench
```

Design §9.4 targets (50k files, warm SQLite, SSD):

| Query | SLO |
| --- | --- |
| `get_tree` depth≤2 | p95 < 200ms |
| symbol search | p95 < 400ms |
| cold index 50k | < 3 min |
| incremental one file | < 50ms |

Do not run the 50k tree in CI.

Recorded on this Windows agent (`BENCH_FILES=1000`):

| Metric | Result | SLO (50k / warm) |
| --- | --- | --- |
| cold index 1k | 1116ms | 50k < 3 min |
| `get_tree` depth≤2 p95 | 8.9ms | < 200ms |
| symbol search p95 | 0.84ms | < 400ms |
| incremental one file | 52ms | < 50ms |
