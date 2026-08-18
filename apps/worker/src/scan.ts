import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";

export type ScannedFile = {
  path: string;
  content: string;
};

const DETECT_NAMES = new Set([
  "agents.md",
  "agent.md",
  "claude.md",
  "conventions.md",
  "codeowners",
  "package.json",
  "pnpm-workspace.yaml",
  "pnpm-workspace.yml",
  "go.work",
  "go.mod",
  "cargo.toml",
]);

const MAX_FILE_BYTES = 256_000;
const MAX_FILES = 200;
const MAX_DEPTH = 6;

function isDetectCandidate(relativePosix: string, name: string): boolean {
  const lower = name.toLowerCase();
  if (DETECT_NAMES.has(lower)) {
    return true;
  }
  if (relativePosix.includes(".cursor/rules/") && lower.endsWith(".mdc")) {
    return true;
  }
  if (relativePosix === ".grok/rules" || relativePosix.startsWith(".grok/")) {
    return lower.endsWith(".md") || lower.endsWith(".mdc") || lower === "rules";
  }
  return false;
}

export async function scanDetectFiles(root: string): Promise<ScannedFile[]> {
  const files: ScannedFile[] = [];
  await walk(root, "", 0, files);
  files.sort((a, b) => a.path.localeCompare(b.path));
  return files;
}

async function walk(
  root: string,
  relative: string,
  depth: number,
  out: ScannedFile[],
): Promise<void> {
  if (out.length >= MAX_FILES || depth > MAX_DEPTH) {
    return;
  }
  const dir = relative ? path.join(root, relative) : root;
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  entries.sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of entries) {
    if (out.length >= MAX_FILES) {
      return;
    }
    if (entry.name === ".git" || entry.name === "node_modules" || entry.name === "dist") {
      continue;
    }
    const childRel = relative ? `${relative}/${entry.name}` : entry.name;
    const childAbs = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      await walk(root, childRel, depth + 1, out);
      continue;
    }
    if (!entry.isFile() || !isDetectCandidate(childRel.replaceAll("\\", "/"), entry.name)) {
      continue;
    }
    let info;
    try {
      info = await stat(childAbs);
    } catch {
      continue;
    }
    if (info.size > MAX_FILE_BYTES) {
      continue;
    }
    const content = await readFile(childAbs, "utf8");
    out.push({ path: childRel.replaceAll("\\", "/"), content });
  }
}
