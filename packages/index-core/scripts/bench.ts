/**
 * Synthetic index bench. CI should use N=1000.
 *
 * Local 50k target (do not run in CI):
 *   BENCH_FILES=50000 pnpm --filter @beacon/index-core bench
 *
 * SLOs (design §9.4, 50k files, warm SQLite, SSD):
 *   get_tree depth<=2 p95 < 200ms
 *   symbol search p95 < 400ms
 *   cold index 50k < 3 min
 *   incremental one file < 50ms
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { IndexCore } from "../src/index-core.js";

const fileCount = Number(process.env.BENCH_FILES ?? 1000);
const root = fs.mkdtempSync(path.join(os.tmpdir(), "beacon-index-bench-"));
const dbPath = path.join(root, "index.sqlite");

function writeTree(n: number): void {
  for (let i = 0; i < n; i += 1) {
    const dir = path.join(root, "src", String(Math.floor(i / 100)));
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(
      path.join(dir, `file-${i}.ts`),
      `export function fn${i}(x: number): number { return x + ${i}; }\n`,
    );
  }
  fs.writeFileSync(path.join(root, "README.md"), "# bench\n");
}

function p95(samples: number[]): number {
  const sorted = [...samples].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))] ?? 0;
}

function timeMs(fn: () => void): number {
  const start = performance.now();
  fn();
  return performance.now() - start;
}

writeTree(fileCount);
const core = new IndexCore({ repoRoot: root, dbPath });
const cold = timeMs(() => {
  core.rebuild();
});

const treeSamples: number[] = [];
const symbolSamples: number[] = [];
for (let i = 0; i < 20; i += 1) {
  treeSamples.push(timeMs(() => {
    core.getTree({ depth: 2 });
  }));
  symbolSamples.push(timeMs(() => {
    core.searchSymbols({ q: "fn", prefix: true });
  }));
}

const extra = path.join(root, "src", "0", "extra.ts");
fs.writeFileSync(extra, "export function extra() { return 1; }\n");
const incremental = timeMs(() => {
  core.index();
});

console.log(
  JSON.stringify(
    {
      files: fileCount,
      coldIndexMs: Math.round(cold),
      getTreeP95Ms: Number(p95(treeSamples).toFixed(2)),
      symbolSearchP95Ms: Number(p95(symbolSamples).toFixed(2)),
      incrementalMs: Number(incremental.toFixed(2)),
      slos: {
        getTreeP95Ms: 200,
        symbolSearchP95Ms: 400,
        coldIndex50kMs: 180_000,
        incrementalOneFileMs: 50,
      },
    },
    null,
    2,
  ),
);

core.close();
fs.rmSync(root, { recursive: true, force: true });
