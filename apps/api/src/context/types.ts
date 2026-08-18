import type { ContextSection } from "@beacon/api-spec";

export const CONTEXT_SCOPE_TYPES = ["project", "repo", "path", "task"] as const;
export const CONTEXT_REVIEW_STATES = ["reviewed", "needs_review"] as const;
export const CONSTRAINT_KINDS = ["must", "must_not", "security", "compliance"] as const;
export const CONSTRAINT_STATUSES = ["proposed", "active", "rejected"] as const;
export const DECISION_STATUSES = ["proposed", "accepted", "superseded", "deprecated"] as const;

export type ContextScopeType = (typeof CONTEXT_SCOPE_TYPES)[number];
export type ContextReviewState = (typeof CONTEXT_REVIEW_STATES)[number];
export type ConstraintKind = (typeof CONSTRAINT_KINDS)[number];
export type ConstraintStatus = (typeof CONSTRAINT_STATUSES)[number];
export type DecisionStatus = (typeof DECISION_STATUSES)[number];

export type ContextNodeRecord = {
  id: string;
  projectId: string;
  repoId: string | null;
  taskId: string | null;
  scopeType: ContextScopeType;
  path: string;
  sections: ContextSection[];
  sectionsText: string;
  source: string;
  sourcePath: string | null;
  reviewState: ContextReviewState;
  updatedByType: string;
  updatedById: string;
  updatedAt: Date;
};

export type ConstraintRecord = {
  id: string;
  projectId: string;
  kind: ConstraintKind;
  body: string;
  scopePath: string;
  status: ConstraintStatus;
  createdAt: Date;
};

export type DecisionPathLink = {
  repoId: string;
  path: string;
};

export type DecisionRecord = {
  id: string;
  projectId: string;
  title: string;
  status: DecisionStatus;
  context: string;
  decision: string;
  consequences: string;
  createdByType: string;
  createdById: string;
  supersededBy: string | null;
  createdAt: Date;
  relatedPaths: DecisionPathLink[];
  relatedTaskIds: string[];
};

export type ContextRevisionTarget = {
  repo_id: string | null;
  path: string;
  task_id: string | null;
};

export type ContextRevisionRecord = {
  id: string;
  projectId: string;
  compiledHash: string;
  compilerVersion: string;
  target: ContextRevisionTarget;
  briefMarkdown: string;
  briefJson: Record<string, unknown>;
  tokenEstimate: number;
  sourceNodeIds: string[];
  sessionId: string | null;
  createdAt: Date;
};

export type CodeOwnerRecord = {
  id: string;
  repoId: string;
  pathPattern: string;
  owners: string[];
  source: string;
};

export type ProjectRepoRecord = {
  id: string;
  projectId: string;
  provider: "github" | "local";
  remoteUrl: string | null;
  defaultBranch: string;
  githubRepoId: bigint | null;
  installationId: bigint | null;
  localRootHint: string | null;
  indexMode: "sidecar" | "bind_mount" | "hosted_clone" | "both";
  lastIndexedSha: string | null;
  lastIndexedAt: Date | null;
};

export function isContextScopeType(value: string): value is ContextScopeType {
  return (CONTEXT_SCOPE_TYPES as readonly string[]).includes(value);
}

export function isConstraintKind(value: string): value is ConstraintKind {
  return (CONSTRAINT_KINDS as readonly string[]).includes(value);
}

export function isConstraintStatus(value: string): value is ConstraintStatus {
  return (CONSTRAINT_STATUSES as readonly string[]).includes(value);
}

export function isDecisionStatus(value: string): value is DecisionStatus {
  return (DECISION_STATUSES as readonly string[]).includes(value);
}
