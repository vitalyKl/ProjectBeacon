import { CompileInputSchema } from "@beacon/api-spec";
import {
  compileSessionBrief,
  exportAgentsMd,
  mergeSections,
  parseImportFiles,
  selectNodes,
  type ImportFile,
} from "@beacon/context";
import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono } from "hono";

import type { AuthDeps } from "../auth/routes.js";
import { errorJson } from "../errors.js";
import { readJson, readObject } from "../http.js";
import { isResponse, requireProjectAccess, requireSession } from "../orgs/routes.js";
import { parsePageQuery, paginateRecords } from "../roadmap/page.js";
import { presentConstraint, presentContextNode, presentDecision, presentMilestoneBrief, presentTaskSummary, sectionsText, toCompileNode } from "./present.js";
import { compileProjectBrief } from "./compile-brief.js";

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
  app.get("/v1/projects/:id/context/nodes", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
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
          node.updatedByType === "user" ? await deps.store.findUserById(node.updatedById) : undefined,
        ),
      ),
    );
    return c.json({ items, next_cursor: result.next_cursor });
  });

  app.post("/v1/projects/:id/context/import", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "write");
    if (access instanceof Response) {
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
        return errorJson(c, 400, "repo_ambiguous", "repo_id is required when the project has multiple repos");
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
        mergedIncoming.set(key, { ...incoming, sections: incoming.sections.map((section) => ({ ...section })) });
        continue;
      }
      existing.sections = mergeSections([
        { id: "existing", project_id: access.project.id, repo_id: repoId, task_id: null, scope_type: incoming.scope_type, path: incoming.path, sections: existing.sections },
        { id: "incoming", project_id: access.project.id, repo_id: repoId, task_id: null, scope_type: incoming.scope_type, path: incoming.path, sections: incoming.sections },
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
        updatedByType: "user",
        updatedById: session.user.id,
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

    return c.json({
      nodes: await Promise.all(
        nodes.map(async (node) => presentContextNode(node, session.user)),
      ),
      code_owners_written: ownersWritten,
    });
  });

  app.get("/v1/projects/:id/context/export/agents-md", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }

    const path = c.req.query("path") ?? "";
    if (path.length > MAX_IMPORT_PATH) {
      return errorJson(c, 400, "unauthorized", "invalid path", { reason: "invalid_body" });
    }
    const resolved = await resolveRepoId(deps, access.project.id, c.req.query("repo_id"));
    if (!resolved.ok) {
      if (resolved.reason === "ambiguous") {
        return errorJson(c, 400, "repo_ambiguous", "repo_id is required when the project has multiple repos");
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
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
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
}
