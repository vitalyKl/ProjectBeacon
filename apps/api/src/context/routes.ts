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
import type { CodeGateway } from "../code/gateway.js";

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

export function mountContext(app: Hono, deps: ContextDeps): void {
  app.get("/v1/projects/:id/context/nodes", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:read");
    if (isResponse(access)) {
      return access;
    }
    const page = parsePageQuery(c);
    if (page instanceof Response) {
      return page;
    }
    const records = await deps.store.listContextNodes(access.project.id);
    const result = paginateRecords(records, page, (item) => item.updatedAt);
    const items = await Promise.all(
      result.items.map(async (node) =>
        presentContextNode(
          node,
          node.updatedByType === "user"
            ? await deps.store.findUserById(node.updatedById)
            : undefined,
        ),
      ),
    );
    return c.json({ items, next_cursor: result.next_cursor });
  });

  app.post("/v1/projects/:id/context/import", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:write");
    if (isResponse(access)) {
      return access;
    }

    const files = parseImportFilesBody(await readJson(c));
    if (!files) {
      return errorJson(c, 400, "unauthorized", "files is required", { reason: "invalid_body" });
    }

    const repoQuery = c.req.query("repo_id");
    const resolved = await resolveRepoId(deps, access.project.id, repoQuery);
    if (!resolved.ok) {
      if (resolved.reason === "ambiguous") {
        return errorJson(
          c,
          400,
          "repo_ambiguous",
          "repo_id is required when the project has multiple repos",
        );
      }
      return errorJson(c, 404, "not_found", "repo not found");
    }

    const parsed = parseImportFiles(files);
    const mergedIncoming = new Map<string, (typeof parsed.nodes)[number]>();
    for (const incoming of parsed.nodes) {
      const repoId = incoming.scope_type === "project" ? null : resolved.repoId;
      const key = `${incoming.scope_type}:${repoId ?? ""}:${incoming.path}`;
      const existing = mergedIncoming.get(key);
      if (!existing) {
        mergedIncoming.set(key, {
          ...incoming,
          sections: incoming.sections.map((section) => ({ ...section })),
        });
        continue;
      }
      existing.sections = mergeSections([
        {
          id: "existing",
          project_id: access.project.id,
          repo_id: repoId,
          task_id: null,
          scope_type: incoming.scope_type,
          path: incoming.path,
          sections: existing.sections,
        },
        {
          id: "incoming",
          project_id: access.project.id,
          repo_id: repoId,
          task_id: null,
          scope_type: incoming.scope_type,
          path: incoming.path,
          sections: incoming.sections,
        },
      ]);
      existing.source_path = incoming.source_path;
      existing.source = incoming.source;
    }

    const existingNodes = await deps.store.listContextNodes(access.project.id);
    const now = deps.clock.now();
    const nodes = [];
    let ordinal = 0;
    for (const incoming of mergedIncoming.values()) {
      const repoId = incoming.scope_type === "project" ? null : resolved.repoId;
      const prior = existingNodes.find(
        (node) =>
          node.scopeType === incoming.scope_type &&
          node.repoId === repoId &&
          node.path === incoming.path &&
          node.taskId === null,
      );
      const sections = prior
        ? mergeSections([
            {
              id: prior.id,
              project_id: access.project.id,
              repo_id: repoId,
              task_id: null,
              scope_type: incoming.scope_type,
              path: incoming.path,
              sections: prior.sections,
            },
            {
              id: "incoming",
              project_id: access.project.id,
              repo_id: repoId,
              task_id: null,
              scope_type: incoming.scope_type,
              path: incoming.path,
              sections: incoming.sections,
            },
          ])
        : incoming.sections;
      const stored = await deps.store.upsertContextNode({
        id: uuidv7(now.getTime() + ordinal),
        projectId: access.project.id,
        repoId,
        taskId: null,
        scopeType: incoming.scope_type,
        path: incoming.path,
        sections,
        sectionsText: sectionsText(sections),
        source: incoming.source,
        sourcePath: incoming.source_path,
        reviewState: "needs_review",
        updatedByType:
          access.actor.kind === "user"
            ? "user"
            : access.actor.kind === "token"
              ? "token"
              : "system",
        updatedById:
          access.actor.kind === "user"
            ? access.actor.user.id
            : access.actor.kind === "token"
              ? access.actor.token.id
              : "worker",
        updatedAt: now,
      });
      nodes.push(stored);
      ordinal += 1;
    }

    let ownersWritten = 0;
    if (resolved.repoId && parsed.code_owners.length > 0) {
      const written = await deps.store.upsertCodeOwners(
        resolved.repoId,
        parsed.code_owners.map((row, index) => ({
          id: uuidv7(now.getTime() + 1_000 + index),
          repoId: resolved.repoId!,
          pathPattern: row.path_pattern,
          owners: row.owners,
          source: row.source,
        })),
      );
      ownersWritten = written.length;
    }

    const actorUser = access.actor.kind === "user" ? access.actor.user : undefined;
    return c.json({
      nodes: await Promise.all(nodes.map(async (node) => presentContextNode(node, actorUser))),
      code_owners_written: ownersWritten,
    });
  });

  app.get("/v1/projects/:id/context/export/agents-md", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:read");
    if (isResponse(access)) {
      return access;
    }

    const path = c.req.query("path") ?? "";
    if (path.length > MAX_IMPORT_PATH) {
      return errorJson(c, 400, "unauthorized", "invalid path", { reason: "invalid_body" });
    }
    const resolved = await resolveRepoId(deps, access.project.id, c.req.query("repo_id"));
    if (!resolved.ok) {
      if (resolved.reason === "ambiguous") {
        return errorJson(
          c,
          400,
          "repo_ambiguous",
          "repo_id is required when the project has multiple repos",
        );
      }
      return errorJson(c, 404, "not_found", "repo not found");
    }

    const nodes = await deps.store.listContextNodes(access.project.id);
    const wantedScope = path ? "path" : resolved.repoId ? "repo" : "project";
    const exact =
      nodes.find(
        (node) =>
          node.scopeType === wantedScope &&
          node.repoId === resolved.repoId &&
          node.path === path &&
          node.taskId === null,
      ) ??
      (wantedScope === "project"
        ? nodes.find(
            (node) =>
              node.scopeType === "repo" &&
              node.repoId === null &&
              node.path === "" &&
              node.taskId === null,
          )
        : undefined);
    const selected = exact
      ? [toCompileNode(exact)]
      : selectNodes(nodes.map(toCompileNode), {
          project_id: access.project.id,
          repo_id: resolved.repoId ?? undefined,
          path,
        });
    const sections = mergeSections(selected);
    const latest = (await deps.store.listContextRevisions(access.project.id))[0];
    const markdown = exportAgentsMd({
      revision: latest?.id ?? "uncompiled",
      scope: { repo_id: resolved.repoId, path },
      sections,
    });
    return c.text(markdown, 200, {
      "content-type": "text/markdown; charset=utf-8",
    });
  });

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
    const compiled = await compileProjectBrief(
      deps.store,
      access.project,
      input,
      now,
      deps.codeGateway,
    );
    if (!compiled.ok) {
      return errorJson(c, 404, "not_found", "task not found");
    }

    const brief = compiled.compiled.brief;
    await deps.store.insertContextRevision({
      id: brief.revision_id,
      projectId: access.project.id,
      compiledHash: brief.compiled_hash,
      compilerVersion: brief.compiler_version,
      target: brief.target,
      briefMarkdown: compiled.compiled.markdown,
      briefJson: brief as unknown as Record<string, unknown>,
      tokenEstimate: brief.budget.used_estimate,
      sourceNodeIds: brief.sources.map((source) => source.node_id),
      sessionId: null,
      createdAt: now,
    });

    return c.json(brief);
  });

  app.get("/v1/projects/:id/context/search", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "context:read");
    if (isResponse(access)) {
      return access;
    }

    const q = c.req.query("q")?.trim() ?? "";
    if (!q) {
      return errorJson(c, 400, "unauthorized", "q is required", { reason: "invalid_query" });
    }
    const limitRaw = c.req.query("limit");
    let limit = PAGINATION_DEFAULT_LIMIT;
    if (limitRaw !== undefined) {
      if (!/^[0-9]+$/.test(limitRaw)) {
        return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_limit" });
      }
      const parsed = Number(limitRaw);
      if (!Number.isInteger(parsed) || parsed < 1 || parsed > PAGINATION_MAX_LIMIT) {
        return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_limit" });
      }
      limit = parsed;
    }

    const needle = q.toLowerCase();
    const nodes = await deps.store.listContextNodes(access.project.id);
    const items = nodes
      .filter((node) => {
        const haystack = `${node.path}\n${node.sectionsText}`.toLowerCase();
        return haystack.includes(needle);
      })
      .slice(0, limit)
      .map((node) => presentContextNode(node));
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

export type ContextDeps = AuthDeps & {
  codeGateway?: CodeGateway;
};
