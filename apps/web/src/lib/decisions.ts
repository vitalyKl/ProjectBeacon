import { apiFetch, fetchAllPages, parseJson, readApiError } from "./api";
import { t, type MessageKey } from "./i18n";

export const DECISION_STATUSES = ["proposed", "accepted", "superseded", "deprecated"] as const;
export const CONSTRAINT_KINDS = ["must", "must_not", "security", "compliance"] as const;
export const CONSTRAINT_STATUSES = ["proposed", "active", "rejected"] as const;

export type DecisionStatus = (typeof DECISION_STATUSES)[number];
export type ConstraintKind = (typeof CONSTRAINT_KINDS)[number];
export type ConstraintStatus = (typeof CONSTRAINT_STATUSES)[number];

export type DecisionPathLink = {
  repo_id: string;
  path: string;
};

export type PublicDecision = {
  id: string;
  project_id: string;
  title: string;
  status: DecisionStatus;
  context: string;
  decision: string;
  consequences: string;
  created_by_type: string;
  created_by_id: string;
  superseded_by: string | null;
  created_at: string;
  related_paths: DecisionPathLink[];
  related_task_ids: string[];
};

export type PublicConstraint = {
  id: string;
  project_id: string;
  kind: ConstraintKind;
  body: string;
  scope_path: string;
  status: ConstraintStatus;
  created_at: string;
};

export type CreateDecisionInput = {
  title: string;
  context: string;
  decision: string;
  consequences?: string;
  status?: DecisionStatus;
};

export type PatchDecisionInput = {
  status: Exclude<DecisionStatus, "proposed">;
  superseded_by?: string | null;
};

export type CreateConstraintInput = {
  kind: ConstraintKind;
  body: string;
  scope_path?: string;
  status?: ConstraintStatus;
};

export async function fetchProjectDecisions(projectId: string): Promise<PublicDecision[]> {
  return fetchAllPages<PublicDecision>(
    `/v1/projects/${encodeURIComponent(projectId)}/decisions`,
    "failed to load decisions",
  );
}

export async function fetchProjectConstraints(projectId: string): Promise<PublicConstraint[]> {
  return fetchAllPages<PublicConstraint>(
    `/v1/projects/${encodeURIComponent(projectId)}/constraints`,
    "failed to load constraints",
  );
}

export async function createDecision(
  projectId: string,
  input: CreateDecisionInput,
  idempotencyKey: string,
): Promise<PublicDecision> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/decisions`, {
    method: "POST",
    headers: { "idempotency-key": idempotencyKey },
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create decision");
  }
  return parseJson<PublicDecision>(res);
}

export async function createConstraint(
  projectId: string,
  input: CreateConstraintInput,
  idempotencyKey: string,
): Promise<PublicConstraint> {
  const res = await apiFetch(`/v1/projects/${encodeURIComponent(projectId)}/constraints`, {
    method: "POST",
    headers: { "idempotency-key": idempotencyKey },
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to create constraint");
  }
  return parseJson<PublicConstraint>(res);
}

export async function patchDecision(
  decisionId: string,
  input: PatchDecisionInput,
): Promise<PublicDecision> {
  const res = await apiFetch(`/v1/decisions/${encodeURIComponent(decisionId)}`, {
    method: "PATCH",
    body: JSON.stringify(input),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to update decision");
  }
  return parseJson<PublicDecision>(res);
}

export async function applyConstraint(constraintId: string): Promise<PublicConstraint> {
  const res = await apiFetch(`/v1/constraints/${encodeURIComponent(constraintId)}/apply`, {
    method: "POST",
    body: JSON.stringify({}),
  });
  if (!res.ok) {
    throw await readApiError(res, "failed to apply constraint");
  }
  return parseJson<PublicConstraint>(res);
}

export function isDecisionStatus(value: string): value is DecisionStatus {
  return (DECISION_STATUSES as readonly string[]).includes(value);
}

export function isConstraintKind(value: string): value is ConstraintKind {
  return (CONSTRAINT_KINDS as readonly string[]).includes(value);
}

export function isConstraintStatus(value: string): value is ConstraintStatus {
  return (CONSTRAINT_STATUSES as readonly string[]).includes(value);
}

export function decisionStatusLabel(status: DecisionStatus): string {
  return t(`decisionStatus.${status}` as MessageKey);
}

export function constraintKindLabel(kind: ConstraintKind): string {
  return t(`constraintKind.${kind}` as MessageKey);
}

export function constraintStatusLabel(status: ConstraintStatus): string {
  return t(`constraintStatus.${status}` as MessageKey);
}

export function canApplyConstraint(constraint: Pick<PublicConstraint, "status">): boolean {
  return constraint.status === "proposed";
}

export function canAcceptDecision(decision: Pick<PublicDecision, "status">): boolean {
  return decision.status === "proposed";
}

export function canSupersedeDecision(decision: Pick<PublicDecision, "status">): boolean {
  return decision.status === "proposed" || decision.status === "accepted";
}

export function canDeprecateDecision(decision: Pick<PublicDecision, "status">): boolean {
  return decision.status === "proposed" || decision.status === "accepted";
}

export function successorDecisionOptions(
  items: PublicDecision[],
  currentId: string,
): PublicDecision[] {
  return items.filter(
    (item) => item.id !== currentId && (item.status === "proposed" || item.status === "accepted"),
  );
}

const DECISION_STATUS_ORDER: Record<DecisionStatus, number> = {
  proposed: 0,
  accepted: 1,
  superseded: 2,
  deprecated: 3,
};

const CONSTRAINT_STATUS_ORDER: Record<ConstraintStatus, number> = {
  proposed: 0,
  active: 1,
  rejected: 2,
};

export function compareNewestFirst(left: string, right: string): number {
  return right.localeCompare(left);
}

export function sortDecisions(items: PublicDecision[]): PublicDecision[] {
  return [...items].sort((left, right) => {
    const statusDelta = DECISION_STATUS_ORDER[left.status] - DECISION_STATUS_ORDER[right.status];
    if (statusDelta !== 0) {
      return statusDelta;
    }
    return compareNewestFirst(left.created_at, right.created_at);
  });
}

export function sortConstraints(items: PublicConstraint[]): PublicConstraint[] {
  return [...items].sort((left, right) => {
    const statusDelta =
      CONSTRAINT_STATUS_ORDER[left.status] - CONSTRAINT_STATUS_ORDER[right.status];
    if (statusDelta !== 0) {
      return statusDelta;
    }
    return compareNewestFirst(left.created_at, right.created_at);
  });
}
