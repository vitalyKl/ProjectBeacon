import type { ConstraintView, DecisionSummary, TaskSummary } from "@beacon/api-spec";

import type {
  ConstraintRecord,
  ContextNodeRecord,
  ContextRevisionRecord,
  DecisionRecord,
} from "./types.js";
import type { UserRecord } from "../auth/store.js";
import type { MilestoneRecord, TaskRecord } from "../roadmap/types.js";

export function presentConstraint(constraint: ConstraintRecord): ConstraintView {
  return {
    id: constraint.id,
    kind: constraint.kind,
    body: constraint.body,
    scope_path: constraint.scopePath,
    status: "active",
  };
}

export function presentConstraintRecord(constraint: ConstraintRecord) {
  return {
    id: constraint.id,
    project_id: constraint.projectId,
    kind: constraint.kind,
    body: constraint.body,
    scope_path: constraint.scopePath,
    status: constraint.status,
    created_at: constraint.createdAt.toISOString(),
  };
}

export function presentDecision(decision: DecisionRecord): DecisionSummary {
  return {
    id: decision.id,
    title: decision.title,
    status: "accepted",
    decision: decision.decision,
    related_paths: decision.relatedPaths.map((path) => path.path),
  };
}

export function presentDecisionRecord(decision: DecisionRecord) {
  return {
    id: decision.id,
    project_id: decision.projectId,
    title: decision.title,
    status: decision.status,
    context: decision.context,
    decision: decision.decision,
    consequences: decision.consequences,
    created_by_type: decision.createdByType,
    created_by_id: decision.createdById,
    superseded_by: decision.supersededBy,
    created_at: decision.createdAt.toISOString(),
    related_paths: decision.relatedPaths.map((path) => ({
      repo_id: path.repoId,
      path: path.path,
    })),
    related_task_ids: [...decision.relatedTaskIds],
  };
}

export function presentTaskSummary(task: TaskRecord): TaskSummary {
  return {
    id: task.id,
    title: task.title,
    status: task.status,
    type: task.type,
    milestone_id: task.milestoneId,
    acceptance_md: acceptanceMarkdown(task),
    linked_paths: task.linkedPaths.map((path) => ({ repo_id: path.repo_id, path: path.path })),
  };
}

export function presentMilestoneBrief(milestone: MilestoneRecord) {
  return {
    id: milestone.id,
    title: milestone.title,
    status: milestone.status,
  };
}

export function toCompileNode(node: ContextNodeRecord) {
  return {
    id: node.id,
    project_id: node.projectId,
    repo_id: node.repoId,
    task_id: node.taskId,
    scope_type: node.scopeType,
    path: node.path,
    sections: node.sections.map((section) => ({ ...section })),
  };
}

export function presentContextNode(node: ContextNodeRecord, actor?: UserRecord) {
  return {
    id: node.id,
    project_id: node.projectId,
    repo_id: node.repoId,
    task_id: node.taskId,
    scope_type: node.scopeType,
    path: node.path,
    sections: node.sections.map((section) => ({ ...section })),
    source: node.source,
    source_path: node.sourcePath,
    review_state: node.reviewState,
    updated_at: node.updatedAt.toISOString(),
    updated_by: {
      type: node.updatedByType,
      id: node.updatedById,
      display: actor?.login ?? node.updatedById,
    },
  };
}

export function presentContextRevisionSummary(revision: ContextRevisionRecord) {
  return {
    id: revision.id,
    project_id: revision.projectId,
    compiled_hash: revision.compiledHash,
    compiler_version: revision.compilerVersion,
    target: { ...revision.target },
    token_estimate: revision.tokenEstimate,
    source_node_ids: [...revision.sourceNodeIds],
    session_id: revision.sessionId,
    created_at: revision.createdAt.toISOString(),
  };
}

export function presentContextRevision(revision: ContextRevisionRecord) {
  return {
    ...presentContextRevisionSummary(revision),
    brief_markdown: revision.briefMarkdown,
    brief: revision.briefJson,
  };
}

export function sectionsText(sections: ContextNodeRecord["sections"]): string {
  return sections.map((section) => `${section.title}\n${section.body_md}`).join("\n\n");
}

function acceptanceMarkdown(task: TaskRecord): string {
  const fromDescription = headingSection(task.description, "Acceptance");
  if (fromDescription) {
    return fromDescription;
  }
  return task.agentBrief;
}

function headingSection(markdown: string, heading: string): string | undefined {
  const pattern = new RegExp(`^#{1,6}\\s+${heading}\\s*$`, "im");
  const match = pattern.exec(markdown);
  if (!match || match.index === undefined) {
    return undefined;
  }
  const start = match.index + match[0].length;
  const rest = markdown.slice(start);
  const next = /^#{1,6}\s+/m.exec(rest);
  const body = (next ? rest.slice(0, next.index) : rest).trim();
  return body.length > 0 ? body : undefined;
}
