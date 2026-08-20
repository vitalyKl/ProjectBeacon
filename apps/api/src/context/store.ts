import type { LabelPatch, LabelRecord } from "../labels/types.js";
import type {
  CodeOwnerRecord,
  ConstraintRecord,
  ContextNodeRecord,
  ContextRevisionRecord,
  DecisionPatch,
  DecisionRecord,
} from "./types.js";

export type ImportContextInput = {
  nodes: ContextNodeRecord[];
  codeOwners?: { repoId: string; rows: CodeOwnerRecord[] };
};

export type ImportContextResult = {
  nodes: ContextNodeRecord[];
  codeOwnersWritten: number;
};

export interface ContextStore {
  listContextNodes(projectId: string): Promise<ContextNodeRecord[]>;
  findContextNodeById(id: string): Promise<ContextNodeRecord | undefined>;
  findContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): Promise<ContextNodeRecord | undefined>;
  upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord>;
  insertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord>;
  importContext(input: ImportContextInput): Promise<ImportContextResult>;
  listActiveConstraints(projectId: string): Promise<ConstraintRecord[]>;
  listConstraints(projectId: string): Promise<ConstraintRecord[]>;
  insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
  listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]>;
  insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord>;
  listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]>;
  findContextRevisionById(id: string): Promise<ContextRevisionRecord | undefined>;
  findConstraintById(id: string): Promise<ConstraintRecord | undefined>;
  createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
  applyConstraint(id: string, appliedAt: Date): Promise<ConstraintRecord | undefined>;
  listDecisions(projectId: string): Promise<DecisionRecord[]>;
  findDecisionById(id: string): Promise<DecisionRecord | undefined>;
  createDecision(decision: DecisionRecord): Promise<DecisionRecord>;
  updateDecision(id: string, patch: DecisionPatch): Promise<DecisionRecord | undefined>;
  listLabels(projectId: string): Promise<LabelRecord[]>;
  findLabelById(id: string): Promise<LabelRecord | undefined>;
  createLabel(label: LabelRecord): Promise<LabelRecord>;
  updateLabel(id: string, patch: LabelPatch): Promise<LabelRecord | undefined>;
  listTaskLabels(taskId: string): Promise<LabelRecord[]>;
  setTaskLabels(taskId: string, labelIds: string[]): Promise<LabelRecord[]>;
}
