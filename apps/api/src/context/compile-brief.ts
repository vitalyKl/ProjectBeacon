import type { BriefHandoff, CompileInput } from "@beacon/api-spec";
import { compileSessionBrief, type CompileResult } from "@beacon/context";
import { uuidv7 } from "@beacon/shared";

import type { AuthStore } from "../auth/store.js";
import { presentHandoff } from "../sessions/present.js";
import {
  presentConstraint,
  presentDecision,
  presentMilestoneBrief,
  presentTaskSummary,
  toCompileNode,
} from "./present.js";

export async function compileProjectBrief(
  store: Pick<
    AuthStore,
    | "findTaskById"
    | "findMilestoneById"
    | "listContextNodes"
    | "listActiveConstraints"
    | "listAcceptedDecisions"
    | "findLatestHandoffByTaskId"
  >,
  project: { id: string; name: string; slug: string },
  input: CompileInput,
  now: Date,
): Promise<{ ok: true; compiled: CompileResult } | { ok: false; reason: "task_not_found" }> {
  let taskSummary = null;
  let milestone = null;
  if (input.task_id) {
    const task = await store.findTaskById(input.task_id);
    if (!task || task.deletedAt || task.projectId !== project.id) {
      return { ok: false, reason: "task_not_found" };
    }
    taskSummary = presentTaskSummary(task);
    if (task.milestoneId) {
      const row = await store.findMilestoneById(task.milestoneId);
      if (row && row.projectId === project.id) {
        milestone = presentMilestoneBrief(row);
      }
    }
  }

  const [nodes, constraints, decisions] = await Promise.all([
    store.listContextNodes(project.id),
    store.listActiveConstraints(project.id),
    store.listAcceptedDecisions(project.id),
  ]);

  let handoff: BriefHandoff | null = null;
  if (input.task_id && (input.include?.handoff ?? true)) {
    const latest = await store.findLatestHandoffByTaskId(input.task_id);
    if (latest) {
      handoff = presentHandoff(latest);
    }
  }

  return {
    ok: true,
    compiled: compileSessionBrief(
      {
        ...input,
        extras: {
          ...input.extras,
          handoff: input.extras?.handoff ?? handoff,
        },
      },
      {
        project: {
          id: project.id,
          name: project.name,
          slug: project.slug,
        },
        nodes: nodes.map(toCompileNode),
        constraints: constraints.map(presentConstraint),
        decisions: decisions.map(presentDecision),
        task: taskSummary,
        milestone,
        revision_id: uuidv7(now.getTime()),
        compiled_at: now.toISOString(),
      },
    ),
  };
}
