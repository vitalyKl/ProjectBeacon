import fs from "node:fs";
import { basename, joinPosix, parentDir } from "./paths.js";

const JS_EXTENSIONS = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs", ".json"];

export interface PackageExports {
  main?: string;
  exports?: unknown;
}

export function collectPackageMaps(
  files: Iterable<string>,
  readJson: (path: string) => PackageExports | null,
): Map<string, string> {
  const map = new Map<string, string>();
  for (const file of files) {
    if (basename(file) !== "package.json") {
      continue;
    }
    const dir = parentDir(file) ?? ".";
    const pkg = readJson(file);
    if (!pkg) {
      continue;
    }
    const target = resolvePackageTarget(pkg);
    if (!target) {
      continue;
    }
    const resolved = normalizeRelative(dir, target);
    if (resolved) {
      map.set(dir, resolved);
    }
  }
  return map;
}

function resolvePackageTarget(pkg: PackageExports): string | null {
  if (typeof pkg.main === "string" && pkg.main.length > 0) {
    return pkg.main;
  }
  return pickExportTarget(pkg.exports);
}

function pickExportTarget(value: unknown): string | null {
  if (typeof value === "string") {
    return value;
  }
  if (!value || typeof value !== "object") {
    return null;
  }
  const record = value as Record<string, unknown>;
  for (const key of [".", "import", "require", "default", "node"]) {
    if (key in record) {
      const nested = pickExportTarget(record[key]);
      if (nested) {
        return nested;
      }
    }
  }
  return null;
}

function normalizeRelative(fromDir: string, spec: string): string | null {
  const cleaned = spec.replace(/^\.\//, "");
  if (cleaned.startsWith("/") || /^[A-Za-z]:/.test(cleaned)) {
    return null;
  }
  return joinPosix(fromDir, cleaned);
}

export function resolveImport(
  fromPath: string,
  spec: string,
  existingFiles: Set<string>,
  packageEntryByDir: Map<string, string>,
): string | null {
  if (!spec || spec.startsWith("node:") || isBarePackage(spec)) {
    return null;
  }
  const fromDir = parentDir(fromPath) ?? ".";
  if (spec.startsWith(".")) {
    return resolveExisting(joinPosix(fromDir, spec), existingFiles, packageEntryByDir);
  }
  return null;
}

function isBarePackage(spec: string): boolean {
  return !spec.startsWith(".") && !spec.startsWith("/");
}

const TS_FROM_JS: Record<string, string[]> = {
  ".js": [".ts", ".tsx", ".js"],
  ".mjs": [".mts", ".mjs"],
  ".cjs": [".cts", ".cjs"],
  ".jsx": [".tsx", ".jsx"],
};

function swapKnownExtension(filePath: string): string[] {
  const dot = filePath.lastIndexOf(".");
  if (dot < 0) {
    return [];
  }
  const ext = filePath.slice(dot);
  const alts = TS_FROM_JS[ext];
  if (!alts) {
    return [];
  }
  const stem = filePath.slice(0, dot);
  return alts.map((next) => `${stem}${next}`);
}

function resolveExisting(
  candidate: string,
  existingFiles: Set<string>,
  packageEntryByDir: Map<string, string>,
): string | null {
  const normalized = collapseDots(candidate);
  if (existingFiles.has(normalized)) {
    return normalized;
  }
  for (const alt of swapKnownExtension(normalized)) {
    if (existingFiles.has(alt)) {
      return alt;
    }
  }
  for (const ext of JS_EXTENSIONS) {
    const withExt = `${normalized}${ext}`;
    if (existingFiles.has(withExt)) {
      return withExt;
    }
  }
  for (const ext of JS_EXTENSIONS) {
    const indexFile = joinPosix(normalized, `index${ext}`);
    if (existingFiles.has(indexFile)) {
      return indexFile;
    }
  }
  const pkgEntry = packageEntryByDir.get(normalized);
  if (pkgEntry && existingFiles.has(pkgEntry)) {
    return pkgEntry;
  }
  return null;
}

function collapseDots(posixPath: string): string {
  const parts: string[] = [];
  for (const part of posixPath.split("/")) {
    if (part === "" || part === ".") {
      continue;
    }
    if (part === "..") {
      parts.pop();
      continue;
    }
    parts.push(part);
  }
  return parts.join("/") || ".";
}

export function readPackageJson(fsPath: string): PackageExports | null {
  try {
    const raw = fs.readFileSync(fsPath, "utf8");
    return JSON.parse(raw) as PackageExports;
  } catch {
    return null;
  }
}
