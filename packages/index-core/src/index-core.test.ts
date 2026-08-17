import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it } from "vitest";
import { IndexCore } from "./index-core.js";

const packagedFixtures = fileURLToPath(new URL("../fixtures", import.meta.url));
const tempRoots: string[] = [];
const cores: IndexCore[] = [];

function tempDb(): string {
  return path.join(os.tmpdir(), `beacon-index-${process.pid}-${Date.now()}-${Math.random()}.sqlite`);
}

function copyDir(from: string, to: string): void {
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === "dist" || entry.name === ".env") {
      continue;
    }
    const src = path.join(from, entry.name);
    const dest = path.join(to, entry.name);
    if (entry.isDirectory()) {
      copyDir(src, dest);
    } else {
      fs.copyFileSync(src, dest);
    }
  }
}

function fixtureRepo(): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "beacon-index-fx-"));
  tempRoots.push(root);
  copyDir(packagedFixtures, root);
  fs.mkdirSync(path.join(root, "node_modules"), { recursive: true });
  fs.writeFileSync(path.join(root, "node_modules", "ignored.js"), "export function shouldNotIndex() {}\n");
  fs.mkdirSync(path.join(root, "dist"), { recursive: true });
  fs.writeFileSync(path.join(root, "dist", "bundle.js"), "export function bundled() {}\n");
  fs.writeFileSync(path.join(root, ".env"), "SECRET=do-not-index\n");
  return root;
}

function openCore(repoRoot = fixtureRepo()): IndexCore {
  const core = new IndexCore({ repoRoot, dbPath: tempDb() });
  cores.push(core);
  return core;
}

afterEach(() => {
  for (const core of cores.splice(0)) {
    core.close();
    try {
      fs.unlinkSync(core.dbPath);
      fs.unlinkSync(`${core.dbPath}-wal`);
      fs.unlinkSync(`${core.dbPath}-shm`);
    } catch {
      // ignore missing sidecar files
    }
  }
  for (const root of tempRoots.splice(0)) {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

describe("IndexCore", () => {
  it("indexes fixtures, skips denylist, and detects NUL binaries", () => {
    const core = openCore();
    const stats = core.index();
    expect(stats.indexed).toBeGreaterThan(0);

    expect(core.readIndexedFileMetadata("src/greet.ts")).not.toBeNull();
    expect(core.readIndexedFileMetadata("node_modules/ignored.js")).toBeNull();
    expect(core.readIndexedFileMetadata("dist/bundle.js")).toBeNull();
    expect(core.readIndexedFileMetadata(".env")).toBeNull();
    expect(core.readIndexedFileMetadata("secret.pem")).toBeNull();

    const binary = core.readIndexedFileMetadata("binary.dat");
    expect(binary?.isBinary).toBe(true);
    const contentHits = core.searchContent({ q: "hi", useRipgrep: false });
    expect(contentHits.every((hit) => hit.path !== "binary.dat")).toBe(true);
  });

  it("supports FTS5 prefix symbol search and point lookup", () => {
    const core = openCore();
    core.index();
    const hits = core.searchSymbols({ q: "gree", prefix: true });
    expect(hits.some((hit) => hit.name === "greet")).toBe(true);
    expect(hits.some((hit) => hit.name === "Greeter")).toBe(true);

    const exact = core.getSymbol({ name: "helper", path: "src/util.js" });
    expect(exact?.kind).toBe("function");
    expect(core.getSymbol({ name: "missing" })).toBeNull();
  });

  it("skips unchanged files on incremental reindex", () => {
    const core = openCore();
    const first = core.index();
    expect(first.indexed).toBeGreaterThan(0);
    const second = core.index();
    expect(second.indexed).toBe(0);
    expect(second.skippedUnchanged).toBeGreaterThan(0);
    expect(second.removed).toBe(0);
  });

  it("resolves same-directory imports and leaves bare specs unresolved", () => {
    const core = openCore();
    core.index();
    const related = core.getRelatedFiles({ path: "src/index.ts" });
    expect(related.some((edge) => edge.toPath === "src/greet.ts")).toBe(true);
    expect(related.some((edge) => edge.toPath === "src/util.js")).toBe(true);
  });

  it("returns directory capsules and changed-scope unions", () => {
    const core = openCore();
    core.index();
    const tree = core.getTree({ root: ".", depth: 2 });
    const root = tree.find((dir) => dir.path === ".");
    expect(root).toBeDefined();
    expect(root?.fileCount).toBeGreaterThan(0);
    expect(root?.important).toContain("README.md");
    expect(tree.some((dir) => dir.path === "src")).toBe(true);

    const scope = core.getChangedScope({
      identifiers: ["greet"],
      linkedPaths: ["src/index.ts"],
      pathPrefixes: ["src/"],
    });
    expect(scope.paths).toContain("src/greet.ts");
    expect(scope.paths).toContain("src/index.ts");
  });

  it("rebuilds from scratch", () => {
    const core = openCore();
    core.index();
    const rebuilt = core.rebuild();
    expect(rebuilt.indexed).toBeGreaterThan(0);
    expect(core.searchSymbols({ q: "Greeter" }).length).toBeGreaterThan(0);
  });
});
