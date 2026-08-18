import type { ConstraintView, DecisionSummary, TaskSummary } from "@beacon/api-spec";

import type { ConstraintRecord, ContextNodeRecord, DecisionRecord } from "./types.js";
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

export function presentDecision(decision: DecisionRecord): DecisionSummary {
  return {
    id: decision.id,
    title: decision.title,
    status: "accepted",
    decision: decision.decision,
    related_paths: [...decision.relatedPaths],
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
