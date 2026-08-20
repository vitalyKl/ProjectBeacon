import type { BriefHandoff, ChangedScope, CompileInput, TreeCapsule } from "@beacon/api-spec";
import { compileSessionBrief, type CompileResult } from "@beacon/context";
import { uuidv7 } from "@beacon/shared";

import type { OrgStore } from "../orgs/store.js";
import type { RepoStore } from "../repos/store.js";
import type { RoadmapStore } from "../roadmap/store.js";
import type { WorkStore } from "../sessions/store.js";
import type { ContextStore } from "./store.js";
import {
  presentChangedScope,
  presentTreeCapsule,
  resolveRepoForCode,
  taskChangedScopeQuery,
  type CodeGateway,
} from "../code/gateway.js";
import { extraCompilePaths } from "../labels/scope.js";
import type { TaskRecord } from "../roadmap/types.js";
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
    RoadmapStore & ContextStore & WorkStore & RepoStore & OrgStore,
    | "findTaskById"
    | "findMilestoneById"
    | "listContextNodes"
    | "listActiveConstraints"
    | "listAcceptedDecisions"
    | "findLatestHandoffByTaskId"
    | "findProjectRepoById"
    | "listProjectRepos"
    | "findProjectById"
    | "listTaskLabels"
  >,
  project: { id: string; name: string; slug: string },
  input: CompileInput,
  now: Date,
  gateway?: CodeGateway,
): Promise<{ ok: true; compiled: CompileResult } | { ok: false; reason: "task_not_found" }> {
  let task: TaskRecord | null = null;
  let taskSummary = null;
  let milestone = null;
  if (input.task_id) {
    const found = await store.findTaskById(input.task_id);
    if (!found || found.deletedAt || found.projectId !== project.id) {
      return { ok: false, reason: "task_not_found" };
    }
    task = found;
    taskSummary = presentTaskSummary(found);
    if (found.milestoneId) {
      const row = await store.findMilestoneById(found.milestoneId);
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

  const attached = task ? await store.listTaskLabels(task.id) : [];
  const extraPaths = extraCompilePaths(attached, input.repo_id);

  let changedScope: ChangedScope | null | undefined = input.extras?.changed_scope;
  let treeCapsule: TreeCapsule | null | undefined = input.extras?.tree_capsule;
  if (gateway && (changedScope === undefined || treeCapsule === undefined)) {
    const resolved = await resolveRepoForCode(store, project.id, input.repo_id);
    if (resolved.ok) {
      if (treeCapsule === undefined && (input.include?.tree_capsule ?? true)) {
        try {
          const tree = await gateway.query(resolved.repo, {
            kind: "tree",
            path: input.path ?? ".",
            depth: 2,
          });
          treeCapsule = presentTreeCapsule(resolved.repo.id, tree);
        } catch {
          treeCapsule = undefined;
        }
      }
      if (changedScope === undefined && task && (input.include?.changed_scope ?? true)) {
        try {
          const scope = await gateway.query(
            resolved.repo,
            taskChangedScopeQuery(task, resolved.repo.id, undefined, extraPaths),
          );
          changedScope = presentChangedScope(resolved.repo.id, scope);
        } catch {
          changedScope = undefined;
        }
      }
    }
  }

  return {
    ok: true,
    compiled: compileSessionBrief(
      {
        ...input,
        extra_paths: [...(input.extra_paths ?? []), ...extraPaths],
        extras: {
          ...input.extras,
          handoff: input.extras?.handoff ?? handoff,
          changed_scope: changedScope,
          tree_capsule: treeCapsule,
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
