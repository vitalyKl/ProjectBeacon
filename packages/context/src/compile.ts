import { createHash } from "node:crypto";

import type {
  BriefHandoff,
  ChangedScope,
  CompileInput,
  ConstraintView,
  ContextSection,
  DecisionSummary,
  SessionBrief,
  TaskSummary,
  TreeCapsule,
} from "@beacon/api-spec";
import { jsLengthDiv4, TOKENIZER_ID } from "@beacon/shared";

export const COMPILER_VERSION = "1.0.0";
export const SCHEMA_VERSION = "1";
export const DEFAULT_BUDGET_TOKENS = 8000;
export const HANDOFF_TOKEN_CAP = 1000;
export const CHANGED_SCOPE_PATH_CAP = 40;
export const DECISIONS_CAP = 10;
export const DECISION_TEXT_CAP = 400;

const NEVER_DROP_SECTION_IDS = new Set(["non_goals", "security"]);

const SECTION_PRIORITY = [
  "pitfalls",
  "conventions",
  "architecture",
  "commands",
  "goals",
  "stack",
  "style",
  "glossary",
  "ownership",
  "custom",
] as const;

const PRIORITY_RANK = new Map<string, number>(SECTION_PRIORITY.map((id, index) => [id, index]));

export type CompileNode = {
  id: string;
  project_id: string;
  repo_id: string | null;
  task_id: string | null;
  scope_type: "project" | "repo" | "path" | "task";
  path: string;
  sections: ContextSection[];
};

export type CompileDocument = {
  project: SessionBrief["project"];
  nodes: CompileNode[];
  constraints: ConstraintView[];
  decisions: DecisionSummary[];
  task: TaskSummary | null;
  milestone: SessionBrief["milestone"];
  revision_id: string;
  compiled_at: string;
};

export type CompileResult = {
  brief: SessionBrief;
  markdown: string;
};

function sectionKey(section: ContextSection): string {
  return section.id === "custom" ? `custom:${section.key}` : section.id;
}

function droppedSectionId(section: ContextSection): string {
  return section.id === "custom" ? `section:custom:${section.key}` : `section:${section.id}`;
}

export function posixPathPrefixes(path: string): string[] {
  const normalized = path.replace(/^\/+/, "").replace(/\/+$/, "");
  if (!normalized) {
    return [];
  }
  const parts = normalized.split("/").filter((part) => part.length > 0);
  const prefixes: string[] = [];
  for (let i = 1; i <= parts.length; i += 1) {
    prefixes.push(parts.slice(0, i).join("/"));
  }
  return prefixes;
}

export function selectNodes(nodes: CompileNode[], input: CompileInput): CompileNode[] {
  const selected: CompileNode[] = [];
  const projectNode = nodes.find((node) => node.scope_type === "project");
  if (projectNode) {
    selected.push(projectNode);
  }

  if (input.repo_id) {
    const repoRoot = nodes.find(
      (node) => node.scope_type === "repo" && node.repo_id === input.repo_id && node.path === "",
    );
    if (repoRoot) {
      selected.push(repoRoot);
    }
    for (const prefix of posixPathPrefixes(input.path ?? "")) {
      const pathNode = nodes.find(
        (node) =>
          node.scope_type === "path" && node.repo_id === input.repo_id && node.path === prefix,
      );
      if (pathNode) {
        selected.push(pathNode);
      }
    }
  }

  if (input.task_id) {
    const taskNode = nodes.find(
      (node) => node.scope_type === "task" && node.task_id === input.task_id,
    );
    if (taskNode) {
      selected.push(taskNode);
    }
  }

  return selected;
}

export function mergeSections(nodes: CompileNode[]): ContextSection[] {
  const merged = new Map<string, ContextSection>();
  for (const node of nodes) {
    for (const section of node.sections) {
      merged.set(sectionKey(section), section);
    }
  }
  return [...merged.values()];
}

function compareSections(left: ContextSection, right: ContextSection): number {
  const leftNever = NEVER_DROP_SECTION_IDS.has(left.id) ? 0 : 1;
  const rightNever = NEVER_DROP_SECTION_IDS.has(right.id) ? 0 : 1;
  if (leftNever !== rightNever) {
    return leftNever - rightNever;
  }
  if (leftNever === 0) {
    if (left.id !== right.id) {
      return left.id === "non_goals" ? -1 : 1;
    }
    return 0;
  }
  const leftRank = PRIORITY_RANK.get(left.id) ?? Number.MAX_SAFE_INTEGER;
  const rightRank = PRIORITY_RANK.get(right.id) ?? Number.MAX_SAFE_INTEGER;
  if (leftRank !== rightRank) {
    return leftRank - rightRank;
  }
  if (left.id === "custom" && right.id === "custom") {
    const byKey = left.key.localeCompare(right.key);
    if (byKey !== 0) {
      return byKey;
    }
  }
  return left.ordinal - right.ordinal;
}

function withOrdinals(sections: ContextSection[]): ContextSection[] {
  return sections.map((section, index) => ({ ...section, ordinal: index }));
}

function truncateChars(text: string, max: number): string {
  return text.length <= max ? text : text.slice(0, max);
}

function activeConstraints(constraints: ConstraintView[]): ConstraintView[] {
  return constraints
    .filter((constraint) => constraint.status === "active")
    .slice()
    .sort((left, right) => left.id.localeCompare(right.id));
}

function acceptedDecisions(decisions: DecisionSummary[]): DecisionSummary[] {
  return decisions
    .filter((decision) => decision.status === "accepted")
    .slice()
    .sort((left, right) => left.id.localeCompare(right.id))
    .slice(0, DECISIONS_CAP)
    .map((decision) => ({
      ...decision,
      decision: truncateChars(decision.decision, DECISION_TEXT_CAP),
    }));
}

function capChangedScope(scope: ChangedScope): ChangedScope {
  const paths = scope.paths.slice(0, CHANGED_SCOPE_PATH_CAP);
  const kept = new Set(paths.map((item) => `${item.repo_id}:${item.path}`));
  const reasons = scope.reasons.filter((reason) => kept.has(`${reason.repo_id}:${reason.path}`));
  return { paths, reasons };
}

function handoffMarkdown(handoff: BriefHandoff): string {
  return `## Handoff\n${handoff.summary}\n\n${handoff.next_steps}\n`;
}

export function truncateHandoff(handoff: BriefHandoff): BriefHandoff {
  if (jsLengthDiv4(handoffMarkdown(handoff)) <= HANDOFF_TOKEN_CAP) {
    return handoff;
  }
  const next: BriefHandoff = {
    ...handoff,
    files_touched: handoff.files_touched.map((path) => ({ ...path })),
    open_questions: [...handoff.open_questions],
  };
  const maxChars = HANDOFF_TOKEN_CAP * 4;
  const header = "## Handoff\n";
  const between = "\n\n";
  const suffix = "\n";
  const nextStepsBudget = Math.max(0, maxChars - header.length - between.length - suffix.length);
  if (jsLengthDiv4(`${header}${next.summary}${between}${suffix}`) <= HANDOFF_TOKEN_CAP) {
    next.next_steps = truncateChars(next.next_steps, nextStepsBudget - next.summary.length);
    if (jsLengthDiv4(handoffMarkdown(next)) <= HANDOFF_TOKEN_CAP) {
      return next;
    }
  }
  next.next_steps = "";
  const summaryBudget = Math.max(0, maxChars - header.length - between.length - suffix.length);
  next.summary = truncateChars(next.summary, summaryBudget);
  return next;
}

export function sessionBriefMarkdown(brief: SessionBrief): string {
  const lines: string[] = [`# ${brief.project.name}`, ""];
  if (brief.task) {
    lines.push("## Task", brief.task.title, "", brief.task.acceptance_md, "");
  }
  if (brief.constraints.length > 0) {
    lines.push("## Constraints");
    for (const constraint of brief.constraints) {
      lines.push(`- [${constraint.kind}] ${constraint.body}`);
    }
    lines.push("");
  }
  for (const section of brief.sections) {
    lines.push(`## ${section.title}`, section.body_md, "");
  }
  if (brief.decisions_relevant.length > 0) {
    lines.push("## Decisions");
    for (const decision of brief.decisions_relevant) {
      lines.push(`### ${decision.title}`, decision.decision, "");
    }
  }
  if (brief.handoff) {
    lines.push("## Handoff", brief.handoff.summary, "", brief.handoff.next_steps, "");
  }
  if (brief.changed_scope) {
    lines.push("## Changed scope");
    for (const path of brief.changed_scope.paths) {
      lines.push(`- ${path.path}`);
    }
    lines.push("");
  }
  if (brief.tree_capsule) {
    lines.push("## Tree");
    for (const entry of brief.tree_capsule.entries) {
      lines.push(`- ${entry.kind} ${entry.path}`);
    }
    lines.push("");
  }
  return `${lines.join("\n").trimEnd()}\n`;
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }
  if (value && typeof value === "object") {
    const record = value as Record<string, unknown>;
    const sorted: Record<string, unknown> = {};
    for (const key of Object.keys(record).sort()) {
      sorted[key] = sortKeys(record[key]);
    }
    return sorted;
  }
  return value;
}

export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortKeys(value));
}

export function hashCompiledBrief(brief: Omit<SessionBrief, "compiled_hash">): string {
  const rest: Record<string, unknown> = { ...brief };
  delete rest["revision_id"];
  return createHash("sha256").update(canonicalJson(rest)).digest("hex");
}

function extrasProvided<T>(value: T | null | undefined): value is T {
  return value !== undefined && value !== null;
}

function sortDropped(ids: Iterable<string>): string[] {
  const rank = (id: string): [number, string] => {
    if (id.startsWith("section:")) {
      const rest = id.slice("section:".length);
      const sectionId = rest.startsWith("custom:") ? "custom" : rest;
      return [PRIORITY_RANK.get(sectionId) ?? 100, id];
    }
    if (id === "decisions") {
      return [200, id];
    }
    if (id === "handoff") {
      return [201, id];
    }
    if (id === "changed_scope") {
      return [202, id];
    }
    if (id === "tree_capsule") {
      return [203, id];
    }
    return [300, id];
  };
  return [...new Set(ids)].sort((left, right) => {
    const [leftRank, leftId] = rank(left);
    const [rightRank, rightId] = rank(right);
    if (leftRank !== rightRank) {
      return leftRank - rightRank;
    }
    return leftId.localeCompare(rightId);
  });
}

export function compileSessionBrief(input: CompileInput, document: CompileDocument): CompileResult {
  const budgetTokens = input.budget_tokens ?? DEFAULT_BUDGET_TOKENS;
  const includeHandoff = input.include?.handoff ?? true;
  const includeChangedScope = input.include?.changed_scope ?? true;
  const includeTreeCapsule = input.include?.tree_capsule ?? true;

  const selectedNodes = selectNodes(document.nodes, input);
  const merged = mergeSections(selectedNodes).slice().sort(compareSections);
  const neverDropSections = merged.filter((section) => NEVER_DROP_SECTION_IDS.has(section.id));
  const optionalSections = merged.filter((section) => !NEVER_DROP_SECTION_IDS.has(section.id));
  const constraints = activeConstraints(document.constraints);
  const decisions = acceptedDecisions(document.decisions);
  const dropped = new Set<string>();

  const target = {
    repo_id: input.repo_id ?? null,
    path: input.path ?? "",
    task_id: input.task_id ?? null,
  };

  const draft = (parts: {
    sections: ContextSection[];
    decisions: DecisionSummary[];
    handoff: BriefHandoff | null;
    changed_scope: ChangedScope | null;
    tree_capsule: TreeCapsule | null;
    overflow: boolean;
    dropped: string[];
    used_estimate: number;
    compiled_hash: string;
  }): SessionBrief => ({
    schema_version: SCHEMA_VERSION,
    compiler_version: COMPILER_VERSION,
    project: document.project,
    compiled_at: document.compiled_at,
    revision_id: document.revision_id,
    compiled_hash: parts.compiled_hash,
    target,
    milestone: document.milestone,
    task: document.task,
    sections: withOrdinals(parts.sections),
    constraints,
    decisions_relevant: parts.decisions,
    handoff: parts.handoff,
    changed_scope: parts.changed_scope,
    tree_capsule: parts.tree_capsule,
    budget: {
      requested: budgetTokens,
      used_estimate: parts.used_estimate,
      tokenizer: TOKENIZER_ID,
      overflow: parts.overflow,
      dropped: parts.dropped,
    },
    sources: selectedNodes.map((node) => ({
      node_id: node.id,
      scope_type: node.scope_type,
      path: node.path,
    })),
  });

  const estimate = (brief: SessionBrief): number => jsLengthDiv4(sessionBriefMarkdown(brief));

  let sections = neverDropSections;
  let keptDecisions: DecisionSummary[] = [];
  let handoff: BriefHandoff | null = null;
  let changedScope: ChangedScope | null = null;
  let treeCapsule: TreeCapsule | null = null;

  const neverDropBrief = draft({
    sections,
    decisions: keptDecisions,
    handoff,
    changed_scope: changedScope,
    tree_capsule: treeCapsule,
    overflow: false,
    dropped: [],
    used_estimate: 0,
    compiled_hash: "",
  });
  const neverDropCost = estimate(neverDropBrief);
  // Never-drop layers still emit when they exceed the budget.
  const overflow = neverDropCost > budgetTokens;

  if (overflow) {
    for (const section of optionalSections) {
      dropped.add(droppedSectionId(section));
    }
    if (decisions.length > 0) {
      dropped.add("decisions");
    }
    if (includeHandoff) {
      dropped.add("handoff");
    }
    if (includeChangedScope) {
      dropped.add("changed_scope");
    }
    if (includeTreeCapsule) {
      dropped.add("tree_capsule");
    }
  } else {
    for (const section of optionalSections) {
      const candidate = draft({
        sections: [...sections, section],
        decisions: keptDecisions,
        handoff,
        changed_scope: changedScope,
        tree_capsule: treeCapsule,
        overflow: false,
        dropped: [],
        used_estimate: 0,
        compiled_hash: "",
      });
      if (estimate(candidate) <= budgetTokens) {
        sections = [...sections, section];
      } else {
        dropped.add(droppedSectionId(section));
      }
    }

    if (decisions.length > 0) {
      const candidate = draft({
        sections,
        decisions,
        handoff,
        changed_scope: changedScope,
        tree_capsule: treeCapsule,
        overflow: false,
        dropped: [],
        used_estimate: 0,
        compiled_hash: "",
      });
      if (estimate(candidate) <= budgetTokens) {
        keptDecisions = decisions;
      } else {
        dropped.add("decisions");
      }
    }

    const extraHandoff = input.extras?.handoff;
    if (!includeHandoff) {
      dropped.add("handoff");
    } else if (extrasProvided(extraHandoff)) {
      const truncated = truncateHandoff(extraHandoff);
      const candidate = draft({
        sections,
        decisions: keptDecisions,
        handoff: truncated,
        changed_scope: changedScope,
        tree_capsule: treeCapsule,
        overflow: false,
        dropped: [],
        used_estimate: 0,
        compiled_hash: "",
      });
      if (estimate(candidate) <= budgetTokens) {
        handoff = truncated;
      } else {
        dropped.add("handoff");
      }
    }

    const extraChanged = input.extras?.changed_scope;
    if (!includeChangedScope) {
      dropped.add("changed_scope");
    } else if (!extrasProvided(extraChanged)) {
      dropped.add("changed_scope");
    } else {
      const capped = capChangedScope(extraChanged);
      const candidate = draft({
        sections,
        decisions: keptDecisions,
        handoff,
        changed_scope: capped,
        tree_capsule: treeCapsule,
        overflow: false,
        dropped: [],
        used_estimate: 0,
        compiled_hash: "",
      });
      if (estimate(candidate) <= budgetTokens) {
        changedScope = capped;
      } else {
        dropped.add("changed_scope");
      }
    }

    const extraTree = input.extras?.tree_capsule;
    if (!includeTreeCapsule) {
      dropped.add("tree_capsule");
    } else if (!extrasProvided(extraTree)) {
      dropped.add("tree_capsule");
    } else {
      const candidate = draft({
        sections,
        decisions: keptDecisions,
        handoff,
        changed_scope: changedScope,
        tree_capsule: extraTree,
        overflow: false,
        dropped: [],
        used_estimate: 0,
        compiled_hash: "",
      });
      if (estimate(candidate) <= budgetTokens) {
        treeCapsule = extraTree;
      } else {
        dropped.add("tree_capsule");
      }
    }
  }

  const unsigned = draft({
    sections,
    decisions: keptDecisions,
    handoff,
    changed_scope: changedScope,
    tree_capsule: treeCapsule,
    overflow,
    dropped: sortDropped(dropped),
    used_estimate: 0,
    compiled_hash: "",
  });
  const markdown = sessionBriefMarkdown(unsigned);
  const usedEstimate = jsLengthDiv4(markdown);
  const hashed = draft({
    sections,
    decisions: keptDecisions,
    handoff,
    changed_scope: changedScope,
    tree_capsule: treeCapsule,
    overflow,
    dropped: sortDropped(dropped),
    used_estimate: usedEstimate,
    compiled_hash: "",
  });
  const withoutHash: Omit<SessionBrief, "compiled_hash"> = {
    schema_version: hashed.schema_version,
    compiler_version: hashed.compiler_version,
    project: hashed.project,
    compiled_at: hashed.compiled_at,
    revision_id: hashed.revision_id,
    target: hashed.target,
    milestone: hashed.milestone,
    task: hashed.task,
    sections: hashed.sections,
    constraints: hashed.constraints,
    decisions_relevant: hashed.decisions_relevant,
    handoff: hashed.handoff,
    changed_scope: hashed.changed_scope,
    tree_capsule: hashed.tree_capsule,
    budget: hashed.budget,
    sources: hashed.sources,
  };
  const brief: SessionBrief = {
    ...hashed,
    compiled_hash: hashCompiledBrief(withoutHash),
  };
  return { brief, markdown: sessionBriefMarkdown(brief) };
}
