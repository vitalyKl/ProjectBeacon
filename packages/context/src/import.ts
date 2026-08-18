import type { ContextSection } from "@beacon/api-spec";

import { sectionFromHeading } from "./aliases.js";

export const CONTEXT_SOURCES = [
  "native",
  "imported_agents_md",
  "imported_claude_md",
  "imported_cursor",
  "imported_grok",
  "imported_conventions_md",
] as const;

export type ContextSource = (typeof CONTEXT_SOURCES)[number];

export type ImportScopeType = "project" | "repo" | "path";

export type ImportFile = {
  path: string;
  content: string;
};

export type ParsedImportNode = {
  scope_type: ImportScopeType;
  path: string;
  sections: ContextSection[];
  source: Exclude<ContextSource, "native">;
  source_path: string;
};

export type ParsedCodeOwner = {
  path_pattern: string;
  owners: string[];
  source: "codeowners";
};

export type ParseImportResult = {
  nodes: ParsedImportNode[];
  code_owners: ParsedCodeOwner[];
};

type Frontmatter = {
  raw: string;
  body: string;
  data: Record<string, unknown>;
};

const HEADING_RE = /^(#{1,6})\s+(.+?)\s*$/gm;

function normalizePosix(path: string): string {
  return path.replaceAll("\\", "/").replace(/^\.?\//, "").replace(/\/+/g, "/");
}

function dirnamePosix(path: string): string {
  const normalized = normalizePosix(path);
  const idx = normalized.lastIndexOf("/");
  return idx === -1 ? "" : normalized.slice(0, idx);
}

function basenamePosix(path: string): string {
  const normalized = normalizePosix(path);
  const idx = normalized.lastIndexOf("/");
  return idx === -1 ? normalized : normalized.slice(idx + 1);
}

function stripLeadingSlash(path: string): string {
  return path.replace(/^\/+/, "");
}

function parseFrontmatter(content: string): Frontmatter {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(content);
  if (!match) {
    return { raw: "", body: content, data: {} };
  }
  const raw = match[1] ?? "";
  const data: Record<string, unknown> = {};
  const lines = raw.split(/\r?\n/);
  let index = 0;
  while (index < lines.length) {
    const line = lines[index] ?? "";
    const kv = /^([A-Za-z0-9_-]+)\s*:\s*(.*)$/.exec(line);
    if (!kv) {
      index += 1;
      continue;
    }
    const key = kv[1] ?? "";
    const rest = (kv[2] ?? "").trim();
    if (rest.startsWith("[") && rest.endsWith("]")) {
      data[key] = rest
        .slice(1, -1)
        .split(",")
        .map((item) => item.trim().replace(/^['"]|['"]$/g, ""))
        .filter((item) => item.length > 0);
      index += 1;
      continue;
    }
    if (rest === "") {
      const list: string[] = [];
      let cursor = index + 1;
      while (cursor < lines.length) {
        const next = lines[cursor] ?? "";
        const item = /^\s*-\s+(.+)$/.exec(next);
        if (!item) {
          break;
        }
        list.push((item[1] ?? "").trim().replace(/^['"]|['"]$/g, ""));
        cursor += 1;
      }
      if (list.length > 0) {
        data[key] = list;
        index = cursor;
        continue;
      }
    }
    if (rest === "" || rest === "|" || rest === ">") {
      const block: string[] = [];
      index += 1;
      while (index < lines.length) {
        const next = lines[index] ?? "";
        if (/^\s+/.test(next) || next.trim() === "") {
          block.push(next.replace(/^\s{2}/, ""));
          index += 1;
          continue;
        }
        break;
      }
      data[key] = block.join("\n").trim();
      continue;
    }
    data[key] = rest.replace(/^['"]|['"]$/g, "");
    index += 1;
  }
  return { raw, body: content.slice(match[0].length), data };
}

function headingSections(markdown: string): ContextSection[] {
  const sections: ContextSection[] = [];
  const matches = [...markdown.matchAll(HEADING_RE)];
  if (matches.length === 0) {
    const body = markdown.trim();
    if (body.length === 0) {
      return [];
    }
    return [sectionFromHeading("Notes", body, 0)];
  }

  const preamble = markdown.slice(0, matches[0]?.index ?? 0).trim();
  let ordinal = 0;
  if (preamble.length > 0) {
    sections.push(sectionFromHeading("Notes", preamble, ordinal));
    ordinal += 1;
  }

  for (let i = 0; i < matches.length; i += 1) {
    const current = matches[i];
    if (!current || current.index === undefined) {
      continue;
    }
    const title = (current[2] ?? "").trim();
    if (title.length === 0) {
      continue;
    }
    const start = current.index + current[0].length;
    const next = matches[i + 1];
    const end = next?.index ?? markdown.length;
    const body = markdown.slice(start, end).trim();
    sections.push(sectionFromHeading(title, body, ordinal));
    ordinal += 1;
  }
  return sections;
}

function asStringList(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.filter((item): item is string => typeof item === "string" && item.trim().length > 0);
  }
  if (typeof value === "string" && value.trim().length > 0) {
    return value
      .split(/[,\n]/)
      .map((item) => item.trim())
      .filter((item) => item.length > 0);
  }
  return [];
}

function globsToPath(globs: string[]): string {
  if (globs.length !== 1) {
    return "";
  }
  const glob = stripLeadingSlash(normalizePosix(globs[0] ?? ""));
  if (!glob || glob.includes("*") || glob.includes("?") || glob.includes("[")) {
    return "";
  }
  return glob.replace(/\/+$/, "");
}

function parseMarkdownFamily(
  file: ImportFile,
  source: Exclude<ContextSource, "native">,
): ParsedImportNode {
  const path = normalizePosix(file.path);
  const directory = dirnamePosix(path);
  return {
    scope_type: directory ? "path" : "repo",
    path: directory,
    sections: headingSections(file.content),
    source,
    source_path: path,
  };
}

function parseRulesFile(
  file: ImportFile,
  source: "imported_cursor" | "imported_grok",
): ParsedImportNode {
  const path = normalizePosix(file.path);
  const parsed = parseFrontmatter(file.content);
  const globs = asStringList(parsed.data["globs"] ?? parsed.data["glob"]);
  const description =
    typeof parsed.data["description"] === "string" ? parsed.data["description"].trim() : "";
  const title =
    typeof parsed.data["title"] === "string" && parsed.data["title"].trim().length > 0
      ? parsed.data["title"].trim()
      : basenamePosix(path).replace(/\.[^.]+$/, "") || "Rules";
  const scopedPath = globsToPath(globs);
  const bodyParts: string[] = [];
  if (description.length > 0) {
    bodyParts.push(description);
  }
  const body = parsed.body.trim();
  if (body.length > 0) {
    bodyParts.push(body);
  }
  const fromHeadings = headingSections(parsed.body);
  const sections =
    fromHeadings.length > 0
      ? fromHeadings
      : [
          sectionFromHeading(
            title,
            bodyParts.join("\n\n"),
            0,
          ),
        ];
  return {
    scope_type: scopedPath ? "path" : "repo",
    path: scopedPath,
    sections,
    source,
    source_path: path,
  };
}

function parseCodeowners(content: string): ParsedCodeOwner[] {
  const rows: ParsedCodeOwner[] = [];
  for (const rawLine of content.split(/\r?\n/)) {
    const withoutComment = rawLine.replace(/(^|\s)#.*$/, "");
    const line = withoutComment.trim();
    if (line.length === 0) {
      continue;
    }
    const parts = line.split(/\s+/).filter((part) => part.length > 0);
    const pattern = parts[0];
    const owners = parts.slice(1);
    if (!pattern || owners.length === 0) {
      continue;
    }
    rows.push({
      path_pattern: pattern,
      owners,
      source: "codeowners",
    });
  }
  return rows;
}

function ownershipSection(owners: ParsedCodeOwner[]): ContextSection {
  const body = owners
    .map((row) => `- \`${row.path_pattern}\`: ${row.owners.join(" ")}`)
    .join("\n");
  return {
    id: "ownership",
    title: "Ownership",
    body_md: body,
    ordinal: 0,
  };
}

function parseJsonObject(content: string): Record<string, unknown> | undefined {
  try {
    const value: unknown = JSON.parse(content);
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      return value as Record<string, unknown>;
    }
  } catch {
    return undefined;
  }
  return undefined;
}

function quotedStrings(content: string): string[] {
  return [...content.matchAll(/"([^"]+)"/g)].map((match) => match[1] ?? "").filter(Boolean);
}

function detectStackHints(file: ImportFile): { labels: string[]; paths: string[] } {
  const path = normalizePosix(file.path);
  const name = basenamePosix(path).toLowerCase();
  const labels: string[] = [];
  const paths: string[] = [];

  if (name === "package.json") {
    const json = parseJsonObject(file.content);
    if (json) {
      if (typeof json["name"] === "string" && json["name"].trim().length > 0) {
        labels.push(json["name"].trim());
      }
      const deps = {
        ...(typeof json["dependencies"] === "object" && json["dependencies"]
          ? json["dependencies"]
          : {}),
        ...(typeof json["devDependencies"] === "object" && json["devDependencies"]
          ? json["devDependencies"]
          : {}),
      };
      for (const key of ["typescript", "react", "next", "hono", "drizzle-orm", "vitest"]) {
        if (key in deps) {
          labels.push(key);
        }
      }
      if (json["packageManager"] || name === "package.json") {
        labels.push("node");
      }
    } else {
      labels.push("node");
    }
    const dir = dirnamePosix(path);
    if (dir) {
      paths.push(dir);
    }
    return { labels: uniqueSorted(labels), paths };
  }

  if (name === "pnpm-workspace.yaml" || name === "pnpm-workspace.yml") {
    labels.push("pnpm");
    for (const quoted of quotedStrings(file.content)) {
      const cleaned = quoted.replace(/\/\*$/, "").replace(/\/+$/, "");
      if (cleaned && !cleaned.includes("*")) {
        paths.push(cleaned);
      }
    }
    return { labels: uniqueSorted(labels), paths: uniqueSorted(paths) };
  }

  if (name === "go.work" || name === "go.mod") {
    labels.push("go");
    for (const line of file.content.split(/\r?\n/)) {
      const use = /^\s*(use\s+)?(\.\/)?([A-Za-z0-9._/-]+)\s*$/.exec(line);
      if (use?.[3] && !use[3].startsWith("github.com") && use[3] !== "go") {
        const dir = use[3].replace(/^\.\//, "");
        if (dir && dir !== ".") {
          paths.push(dir);
        }
      }
    }
    return { labels: uniqueSorted(labels), paths: uniqueSorted(paths) };
  }

  if (name === "cargo.toml") {
    labels.push("rust");
    const packageName = /^\s*name\s*=\s*"([^"]+)"/m.exec(file.content);
    if (packageName?.[1]) {
      labels.push(packageName[1]);
    }
    const dir = dirnamePosix(path);
    if (dir) {
      paths.push(dir);
    }
    return { labels: uniqueSorted(labels), paths };
  }

  return { labels: [], paths: [] };
}

function uniqueSorted(values: string[]): string[] {
  return [...new Set(values)].sort((a, b) => a.localeCompare(b));
}

function classifyFile(path: string):
  | { kind: "agents" }
  | { kind: "claude" }
  | { kind: "conventions" }
  | { kind: "cursor" }
  | { kind: "grok" }
  | { kind: "codeowners" }
  | { kind: "stack" }
  | { kind: "skip" } {
  const normalized = normalizePosix(path);
  const base = basenamePosix(normalized);
  const lowerBase = base.toLowerCase();
  if (lowerBase === "agents.md" || lowerBase === "agent.md") {
    return { kind: "agents" };
  }
  if (lowerBase === "claude.md") {
    return { kind: "claude" };
  }
  if (lowerBase === "conventions.md") {
    return { kind: "conventions" };
  }
  if (normalized.includes(".cursor/rules/") && lowerBase.endsWith(".mdc")) {
    return { kind: "cursor" };
  }
  if (normalized === ".grok/rules" || normalized.startsWith(".grok/")) {
    if (
      lowerBase.endsWith(".md") ||
      lowerBase.endsWith(".mdc") ||
      lowerBase === "rules" ||
      normalized === ".grok/rules"
    ) {
      return { kind: "grok" };
    }
  }
  if (lowerBase === "codeowners" || normalized.endsWith("/CODEOWNERS") || normalized === "CODEOWNERS") {
    return { kind: "codeowners" };
  }
  if (
    lowerBase === "package.json" ||
    lowerBase === "pnpm-workspace.yaml" ||
    lowerBase === "pnpm-workspace.yml" ||
    lowerBase === "go.work" ||
    lowerBase === "go.mod" ||
    lowerBase === "cargo.toml"
  ) {
    return { kind: "stack" };
  }
  return { kind: "skip" };
}

function mergeStackSections(existing: ContextSection | undefined, labels: string[]): ContextSection {
  const prior = existing?.body_md
    .split(/\r?\n/)
    .map((line) => line.replace(/^- /, "").trim())
    .filter((line) => line.length > 0);
  const merged = uniqueSorted([...(prior ?? []), ...labels]);
  return {
    id: "stack",
    title: "Stack",
    body_md: merged.map((label) => `- ${label}`).join("\n"),
    ordinal: 0,
  };
}

export function parseImportFiles(files: ImportFile[]): ParseImportResult {
  const nodes: ParsedImportNode[] = [];
  const codeOwners: ParsedCodeOwner[] = [];
  const stackByPath = new Map<string, ParsedImportNode>();

  const sorted = [...files].sort((a, b) => normalizePosix(a.path).localeCompare(normalizePosix(b.path)));
  for (const file of sorted) {
    const classified = classifyFile(file.path);
    if (classified.kind === "skip") {
      continue;
    }
    if (classified.kind === "agents") {
      nodes.push(parseMarkdownFamily(file, "imported_agents_md"));
      continue;
    }
    if (classified.kind === "claude") {
      nodes.push(parseMarkdownFamily(file, "imported_claude_md"));
      continue;
    }
    if (classified.kind === "conventions") {
      nodes.push(parseMarkdownFamily(file, "imported_conventions_md"));
      continue;
    }
    if (classified.kind === "cursor") {
      nodes.push(parseRulesFile(file, "imported_cursor"));
      continue;
    }
    if (classified.kind === "grok") {
      nodes.push(parseRulesFile(file, "imported_grok"));
      continue;
    }
    if (classified.kind === "codeowners") {
      const owners = parseCodeowners(file.content);
      codeOwners.push(...owners);
      if (owners.length > 0) {
        nodes.push({
          scope_type: "repo",
          path: "",
          sections: [ownershipSection(owners)],
          source: "imported_agents_md",
          source_path: normalizePosix(file.path),
        });
      }
      continue;
    }
    const detected = detectStackHints(file);
    if (detected.labels.length === 0 && detected.paths.length === 0) {
      continue;
    }
    const labels = detected.labels.length > 0 ? detected.labels : ["unknown"];
    const targetPaths = detected.paths.length > 0 ? detected.paths : [""];
    for (const target of targetPaths) {
      const key = target;
      const existing = stackByPath.get(key);
      const section = mergeStackSections(existing?.sections[0], labels);
      stackByPath.set(key, {
        scope_type: target ? "path" : "repo",
        path: target,
        sections: [section],
        source: existing?.source ?? "imported_agents_md",
        source_path: existing?.source_path ?? normalizePosix(file.path),
      });
    }
  }

  nodes.push(...stackByPath.values());
  return { nodes, code_owners: codeOwners };
}
