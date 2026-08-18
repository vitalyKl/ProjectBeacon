import fs from "node:fs";

import { toFsPath } from "./paths.js";

export type CodeOwnerRule = {
  pathPattern: string;
  owners: string[];
};

export function parseCodeowners(source: string): CodeOwnerRule[] {
  const rules: CodeOwnerRule[] = [];
  for (const raw of source.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }
    const parts = line.split(/\s+/);
    const pattern = parts[0];
    if (!pattern) {
      continue;
    }
    const owners = parts.slice(1).filter((part) => part.startsWith("@") || part.includes("/"));
    if (owners.length === 0) {
      continue;
    }
    rules.push({ pathPattern: pattern, owners });
  }
  return rules;
}

export function matchCodeowners(path: string, rules: readonly CodeOwnerRule[]): string[] {
  let matched: string[] = [];
  for (const rule of rules) {
    if (codeownerMatches(path, rule.pathPattern)) {
      matched = [...rule.owners];
    }
  }
  return matched;
}

function codeownerMatches(filePath: string, pattern: string): boolean {
  if (pattern === "*" || pattern === "**") {
    return true;
  }
  const normalized = pattern.replace(/^\//, "").replace(/\/$/, "");
  const target = filePath.replace(/^\//, "");
  if (normalized.endsWith("/*")) {
    const prefix = normalized.slice(0, -1);
    return target.startsWith(prefix) && !target.slice(prefix.length).includes("/");
  }
  if (normalized.endsWith("/**") || normalized.endsWith("/")) {
    const prefix = normalized.replace(/\/\*\*$/, "/");
    return target === prefix.replace(/\/$/, "") || target.startsWith(prefix);
  }
  if (normalized.endsWith("*")) {
    return target.startsWith(normalized.slice(0, -1));
  }
  return target === normalized || target.startsWith(`${normalized}/`);
}

export function ownersForPath(repoRoot: string, filePath: string): string[] {
  for (const candidate of ["CODEOWNERS", ".github/CODEOWNERS", "docs/CODEOWNERS"]) {
    const abs = toFsPath(repoRoot, candidate);
    try {
      const source = fs.readFileSync(abs, "utf8");
      return matchCodeowners(filePath, parseCodeowners(source));
    } catch {
      continue;
    }
  }
  return [];
}
