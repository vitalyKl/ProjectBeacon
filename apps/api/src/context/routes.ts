import { CompileInputSchema } from "@beacon/api-spec";
import { compileSessionBrief } from "@beacon/context";
import { isUuid, uuidv7 } from "@beacon/shared";
import type { Hono } from "hono";

import type { AuthDeps } from "../auth/routes.js";
import { errorJson } from "../errors.js";
import { readObject } from "../http.js";
import { isResponse, requireProjectAccess, requireSession } from "../orgs/routes.js";
import {
  presentConstraint,
  presentDecision,
  presentMilestoneBrief,
  presentTaskSummary,
  toCompileNode,
} from "./present.js";

export function mountContext(app: Hono, deps: AuthDeps): void {
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
    if (input.task_id && !isUuid(input.task_id)) {
      return errorJson(c, 404, "not_found", "task not found");
    }

    let taskSummary = null;
    let milestone = null;
    if (input.task_id) {
      const task = await deps.store.findTaskById(input.task_id);
      if (!task || task.deletedAt || task.projectId !== access.project.id) {
        return errorJson(c, 404, "not_found", "task not found");
      }
      taskSummary = presentTaskSummary(task);
      if (task.milestoneId) {
        const row = await deps.store.findMilestoneById(task.milestoneId);
        if (row && row.projectId === access.project.id) {
          milestone = presentMilestoneBrief(row);
        }
      }
    }

    const now = deps.clock.now();
    const [nodes, constraints, decisions] = await Promise.all([
      deps.store.listContextNodes(access.project.id),
      deps.store.listActiveConstraints(access.project.id),
      deps.store.listAcceptedDecisions(access.project.id),
    ]);

    const compiled = compileSessionBrief(input, {
      project: {
        id: access.project.id,
        name: access.project.name,
        slug: access.project.slug,
      },
      nodes: nodes.map(toCompileNode),
      constraints: constraints.map(presentConstraint),
      decisions: decisions.map(presentDecision),
      task: taskSummary,
      milestone,
      revision_id: uuidv7(now.getTime()),
      compiled_at: now.toISOString(),
    });

    await deps.store.insertContextRevision({
      id: compiled.brief.revision_id,
      projectId: access.project.id,
      compiledHash: compiled.brief.compiled_hash,
      compilerVersion: compiled.brief.compiler_version,
      target: compiled.brief.target,
      briefMarkdown: compiled.markdown,
      briefJson: compiled.brief as unknown as Record<string, unknown>,
      tokenEstimate: compiled.brief.budget.used_estimate,
      sourceNodeIds: compiled.brief.sources.map((source) => source.node_id),
      sessionId: null,
      createdAt: now,
    });

    return c.json(compiled.brief);
  });
}
