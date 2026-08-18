import { CompileInputSchema, ContextSectionSchema, type ContextSection } from "@beacon/api-spec";
import { compileSessionBrief, exportAgentsMd, mergeSections, parseImportFiles, selectNodes, type ImportFile } from "@beacon/context";
import { isUuid, uuidv7, PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT } from "@beacon/shared";
import type { Hono } from "hono";
import type { AuthDeps } from "../auth/routes.js";
import { errorJson } from "../errors.js";
import { readJson, readObject, parseOptionalString } from "../http.js";
import { isResponse, requireProjectAccess, requireSession } from "../orgs/routes.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentConstraint, presentContextNode, presentDecision, presentMilestoneBrief, presentTaskSummary, sectionsText, toCompileNode, presentContextRevision, presentContextRevisionSummary } from "./present.js";
import { compileProjectBrief } from "./compile-brief.js";
import { type ContextNodeRecord, isContextReviewState, isContextScopeType, type ContextReviewState, type ContextScopeType } from "./types.js";
import { requireProjectActor } from "../auth/access.js";

const MAX_IMPORT_FILES = 200;
const MAX_IMPORT_PATH = 1024;
const MAX_IMPORT_CONTENT = 256_000;

function parseImportFilesBody(body: unknown): ImportFile[] | undefined {
  const list = Array.isArray(body)
    ? body
    : body !== null && typeof body === "object" && !Array.isArray(body)
      ? (body as Record<string, unknown>)["files"]
      : undefined;
  if (!Array.isArray(list) || list.length > MAX_IMPORT_FILES) {
    return undefined;
  }
  const files: ImportFile[] = [];
  for (const item of list) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return undefined;
    }
    const record = item as Record<string, unknown>;
    if (typeof record["path"] !== "string" || typeof record["content"] !== "string") {
      return undefined;
    }
    const path = record["path"].replaceAll("\\", "/").trim();
    if (path.length === 0 || path.length > MAX_IMPORT_PATH) {
      return undefined;
    }
    if (record["content"].length > MAX_IMPORT_CONTENT) {
      return undefined;
    }
    files.push({ path, content: record["content"] });
  }
  return files;
}

async function resolveRepoId(
  deps: AuthDeps,
  projectId: string,
  requested: string | undefined,
): Promise<{ ok: true; repoId: string | null } | { ok: false; reason: "not_found" | "ambiguous" }> {
  if (requested) {
    if (!isUuid(requested)) {
      return { ok: false, reason: "not_found" };
    }
    const repo = await deps.store.findProjectRepoById(requested);
    if (!repo || repo.projectId !== projectId) {
      return { ok: false, reason: "not_found" };
    }
    return { ok: true, repoId: repo.id };
  }
  const repos = await deps.store.listProjectRepos(projectId);
  if (repos.length === 0) {
    return { ok: true, repoId: null };
  }
  if (repos.length === 1) {
    return { ok: true, repoId: repos[0]?.id ?? null };
  }
  return { ok: false, reason: "ambiguous" };
}

export function mountContext(app: Hono, deps: AuthDeps): void {
  app.post("/v1/projects/:id/context/compile", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:read");
    if (isResponse(access)) {
      return access;
    }

    const raw = (await readObject(c)) ?? {};
    const parsed = CompileInputSchema.safeParse({
      ...raw,
      project_id: typeof raw["project_id"] === "string" ? raw["project_id"] : access.project.id,
    });
    if (!parsed.success) {
      return errorJson(c, 400, "unauthorized", "invalid compile input", { reason: "invalid_body" });
    }
    const input = parsed.data;
    if (input.project_id !== access.project.id) {
      return errorJson(c, 400, "unauthorized", "project_id does not match path", {
        reason: "invalid_body",
      });
    }

    const now = deps.clock.now();
    const compiled = await compileProjectBrief(deps.store, access.project, input, now);
    if (!compiled.ok) {
      return errorJson(c, 404, "not_found", "task not found");
    }
    const { compiled: result } = compiled;

    await deps.store.insertContextRevision({
      id: result.brief.revision_id,
      projectId: access.project.id,
      compiledHash: result.brief.compiled_hash,
      compilerVersion: result.brief.compiler_version,
      target: result.brief.target,
      briefMarkdown: result.markdown,
      briefJson: result.brief as unknown as Record<string, unknown>,
      tokenEstimate: result.brief.budget.used_estimate,
      sourceNodeIds: result.brief.sources.map((source) => source.node_id),
      sessionId: null,
      createdAt: now,
    });

    return c.json(result.brief);
  });

  app.get("/v1/projects/:id/context/search", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:read");
    if (isResponse(access)) {
      return access;
    }
    const q = c.req.query("q")?.trim() ?? "";
    if (q.length === 0) {
      return errorJson(c, 400, "unauthorized", "q is required", { reason: "invalid_query" });
    }
    const limit = parseSearchLimit(c.req.query("limit"));
    if (limit === undefined) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_limit" });
    }
    const nodes = await deps.store.listContextNodes(access.project.id);
    const items = nodes.filter((node) => nodeMatchesQuery(node, q)).slice(0, limit).map(toCompileNode);
    return c.json({ items, next_cursor: null });
  });
}

const KNOWN_SECTION_IDS = [
  "goals",
  "non_goals",
  "architecture",
  "conventions",
  "glossary",
  "ownership",
  "pitfalls",
  "commands",
  "stack",
  "security",
  "style",
] as const;

function parseNativeSections(value: unknown): ContextNodeRecord["sections"] | undefined {
  if (!Array.isArray(value) || value.length === 0) {
    return undefined;
  }
  const sections: ContextNodeRecord["sections"] = [];
  for (const [index, item] of value.entries()) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return undefined;
    }
    const record = item as Record<string, unknown>;
    const title = typeof record["title"] === "string" ? record["title"].trim() : "";
    const bodyMd = typeof record["body_md"] === "string" ? record["body_md"] : "";
    const id = typeof record["id"] === "string" ? record["id"] : "custom";
    if (!title) {
      return undefined;
    }
    if (id === "custom") {
      const key =
        typeof record["key"] === "string" && record["key"].trim().length > 0
          ? record["key"].trim()
          : `section-${index + 1}`;
      sections.push({ id: "custom", key, title, body_md: bodyMd, ordinal: index });
      continue;
    }
    if (!(KNOWN_SECTION_IDS as readonly string[]).includes(id)) {
      return undefined;
    }
    sections.push({
      id: id as (typeof KNOWN_SECTION_IDS)[number],
      title,
      body_md: bodyMd,
      ordinal: index,
    });
  }
  return sections;
}

function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}
const NATIVE_CREATE_SCOPES = new Set<ContextScopeType>(["project", "repo", "path"]);
const NATIVE_SOURCE = "native";

function parseSections(value: unknown): ContextSection[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }
  const sections: ContextSection[] = [];
  for (const item of value) {
    const parsed = ContextSectionSchema.safeParse(item);
    if (!parsed.success) {
      return undefined;
    }
    sections.push(parsed.data);
  }
  return sections;
}

function parseNullableUuid(value: unknown): string | null | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (value === null) {
    return null;
  }
  if (typeof value === "string" && isUuid(value)) {
    return value;
  }
  return undefined;
}

function normalizeNodePath(value: string): string | undefined {
  const path = value.replaceAll("\\", "/").replace(/^\/+/, "").replace(/\/+$/, "");
  if (path.length > MAX_IMPORT_PATH) {
    return undefined;
  }
  return path;
}

function parseSearchLimit(raw: string | undefined): number | undefined {
  if (raw === undefined || raw === "") {
    return PAGINATION_DEFAULT_LIMIT;
  }
  if (!/^[0-9]+$/.test(raw)) {
    return undefined;
  }
  const parsed = Number(raw);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > PAGINATION_MAX_LIMIT) {
    return undefined;
  }
  return parsed;
}

function nodeMatchesQuery(
  node: {
    path: string;
    sectionsText: string;
    sections: { title: string; body_md: string }[];
  },
  query: string,
): boolean {
  const needle = query.toLowerCase();
  if (
    node.path.toLowerCase().includes(needle) ||
    node.sectionsText.toLowerCase().includes(needle)
  ) {
    return true;
  }
  return node.sections.some(
    (section) =>
      section.title.toLowerCase().includes(needle) ||
      section.body_md.toLowerCase().includes(needle),
  );
}
