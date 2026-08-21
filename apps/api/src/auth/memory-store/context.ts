import { UniqueViolationError } from "../errors.js";
import type { LabelRecord, LabelPatch } from "../../labels/types.js";
import type { ContextNodeRecord, ConstraintRecord, DecisionRecord, DecisionPatch, ContextRevisionRecord } from "../../context/types.js";
import type { ImportContextInput, ImportContextResult } from "../../context/store.js";
import { cloneLabel, cloneContextNode, cloneConstraint, cloneDecision, cloneContextRevision } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryContext<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryContext extends Base {
  async listContextNodes(projectId: string): Promise<ContextNodeRecord[]> {
    const result: ContextNodeRecord[] = [];
    for (const node of this.contextNodes.values()) {
      if (node.projectId === projectId) {
        result.push(cloneContextNode(node));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async findContextNodeById(id: string): Promise<ContextNodeRecord | undefined> {
    const node = this.contextNodes.get(id);
    return node ? cloneContextNode(node) : undefined;
  }

  async findContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): Promise<ContextNodeRecord | undefined> {
    const node = this.matchContextNodeByScope(scope);
    return node ? cloneContextNode(node) : undefined;
  }

  async upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return this.enqueueWrite(() => this.upsertContextNodeUnlocked(node));
  }

  async importContext(input: ImportContextInput): Promise<ImportContextResult> {
    return this.enqueueWrite(() => {
      const nodes = input.nodes.map((node) => this.upsertContextNodeUnlocked(node));
      let codeOwnersWritten = 0;
      if (input.codeOwners) {
        codeOwnersWritten = this.upsertCodeOwnersUnlocked(
          input.codeOwners.repoId,
          input.codeOwners.rows,
        ).length;
      }
      return { nodes, codeOwnersWritten };
    });
  }

  async insertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return this.enqueueWrite(() => {
      const existing = this.matchContextNodeByScope(node);
      if (existing) {
        return cloneContextNode(existing);
      }
      this.contextNodes.set(node.id, cloneContextNode(node));
      return cloneContextNode(node);
    });
  }

  async listActiveConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const result: ConstraintRecord[] = [];
    for (const constraint of this.constraints.values()) {
      if (constraint.projectId === projectId && constraint.status === "active") {
        result.push(cloneConstraint(constraint));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async listConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const result: ConstraintRecord[] = [];
    for (const constraint of this.constraints.values()) {
      if (constraint.projectId === projectId) {
        result.push(cloneConstraint(constraint));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    return this.createConstraint(constraint);
  }

  async listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]> {
    const result: DecisionRecord[] = [];
    for (const decision of this.decisions.values()) {
      if (decision.projectId === projectId && decision.status === "accepted") {
        result.push(cloneDecision(decision));
      }
    }
    result.sort((a, b) => a.id.localeCompare(b.id));
    return result;
  }

  async insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord> {
    return this.enqueueWrite(() => {
      if (this.contextRevisions.has(revision.id)) {
        throw new UniqueViolationError("context_revisions_pkey");
      }
      this.contextRevisions.set(revision.id, cloneContextRevision(revision));
      return cloneContextRevision(revision);
    });
  }

  async listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]> {
    const result: ContextRevisionRecord[] = [];
    for (const revision of this.contextRevisions.values()) {
      if (revision.projectId === projectId) {
        result.push(cloneContextRevision(revision));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async findContextRevisionById(id: string): Promise<ContextRevisionRecord | undefined> {
    const revision = this.contextRevisions.get(id);
    return revision ? cloneContextRevision(revision) : undefined;
  }

  matchContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): ContextNodeRecord | undefined {
    for (const existing of this.contextNodes.values()) {
      if (
        existing.projectId === scope.projectId &&
        existing.scopeType === scope.scopeType &&
        existing.repoId === scope.repoId &&
        existing.path === scope.path &&
        existing.taskId === scope.taskId
      ) {
        return existing;
      }
    }
    return undefined;
  }

  upsertContextNodeUnlocked(node: ContextNodeRecord): ContextNodeRecord {
    const existing = this.matchContextNodeByScope(node);
    if (existing) {
      existing.sections = node.sections.map((section) => ({ ...section }));
      existing.sectionsText = node.sectionsText;
      existing.source = node.source;
      existing.sourcePath = node.sourcePath;
      existing.reviewState = node.reviewState;
      existing.updatedByType = node.updatedByType;
      existing.updatedById = node.updatedById;
      existing.updatedAt = new Date(node.updatedAt);
      return cloneContextNode(existing);
    }
    this.contextNodes.set(node.id, cloneContextNode(node));
    return cloneContextNode(node);
  }

  async findConstraintById(id: string): Promise<ConstraintRecord | undefined> {
    const constraint = this.constraints.get(id);
    return constraint ? cloneConstraint(constraint) : undefined;
  }

  async createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    return this.enqueueWrite(() => this.insertConstraintUnlocked(constraint));
  }

  async applyConstraint(id: string, appliedAt: Date): Promise<ConstraintRecord | undefined> {
    return this.enqueueWrite(() => {
      const constraint = this.constraints.get(id);
      if (!constraint || constraint.status !== "proposed") {
        return undefined;
      }
      constraint.status = "active";
      void appliedAt;
      return cloneConstraint(constraint);
    });
  }

  async listDecisions(projectId: string): Promise<DecisionRecord[]> {
    const result: DecisionRecord[] = [];
    for (const decision of this.decisions.values()) {
      if (decision.projectId === projectId) {
        result.push(cloneDecision(decision));
      }
    }
    result.sort(
      (a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id),
    );
    return result;
  }

  async listLabels(projectId: string): Promise<LabelRecord[]> {
    const result: LabelRecord[] = [];
    for (const label of this.labels.values()) {
      if (label.projectId === projectId) {
        result.push(cloneLabel(label));
      }
    }
    result.sort(
      (a, b) =>
        a.status.localeCompare(b.status) ||
        a.name.localeCompare(b.name) ||
        a.id.localeCompare(b.id),
    );
    return result;
  }

  async findLabelById(id: string): Promise<LabelRecord | undefined> {
    const label = this.labels.get(id);
    return label ? cloneLabel(label) : undefined;
  }

  async createLabel(label: LabelRecord): Promise<LabelRecord> {
    return this.enqueueWrite(() => {
      for (const existing of this.labels.values()) {
        if (existing.projectId === label.projectId && existing.slug === label.slug) {
          throw new UniqueViolationError("labels_project_slug");
        }
      }
      this.labels.set(label.id, cloneLabel(label));
      return cloneLabel(label);
    });
  }

  async updateLabel(id: string, patch: LabelPatch): Promise<LabelRecord | undefined> {
    return this.enqueueWrite(() => {
      const label = this.labels.get(id);
      if (!label) {
        return undefined;
      }
      if (patch.slug !== undefined && patch.slug !== label.slug) {
        for (const existing of this.labels.values()) {
          if (
            existing.id !== id &&
            existing.projectId === label.projectId &&
            existing.slug === patch.slug
          ) {
            throw new UniqueViolationError("labels_project_slug");
          }
        }
        label.slug = patch.slug;
      }
      if (patch.name !== undefined) {
        label.name = patch.name;
      }
      if (patch.description !== undefined) {
        label.description = patch.description;
      }
      if (patch.color !== undefined) {
        label.color = patch.color;
      }
      if (patch.status !== undefined) {
        label.status = patch.status;
      }
      if (patch.paths !== undefined) {
        label.paths = patch.paths.map((path) => ({ ...path }));
      }
      return cloneLabel(label);
    });
  }

  async listTaskLabels(taskId: string): Promise<LabelRecord[]> {
    const ids = this.taskLabelIds.get(taskId) ?? [];
    const result: LabelRecord[] = [];
    for (const id of ids) {
      const label = this.labels.get(id);
      if (label) {
        result.push(cloneLabel(label));
      }
    }
    result.sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    return result;
  }

  async setTaskLabels(taskId: string, labelIds: string[]): Promise<LabelRecord[]> {
    return this.enqueueWrite(() => this.replaceTaskLabelsUnlocked(taskId, labelIds));
  }

  async findDecisionById(id: string): Promise<DecisionRecord | undefined> {
    const decision = this.decisions.get(id);
    return decision ? cloneDecision(decision) : undefined;
  }

  async createDecision(decision: DecisionRecord): Promise<DecisionRecord> {
    return this.enqueueWrite(() => this.insertDecisionUnlocked(decision));
  }

  async updateDecision(id: string, patch: DecisionPatch): Promise<DecisionRecord | undefined> {
    return this.enqueueWrite(() => {
      const decision = this.decisions.get(id);
      if (!decision) {
        return undefined;
      }
      decision.status = patch.status;
      if (patch.supersededBy !== undefined) {
        decision.supersededBy = patch.supersededBy;
      }
      return cloneDecision(decision);
    });
  }

  seedConstraint(constraint: ConstraintRecord): void {
    this.constraints.set(constraint.id, cloneConstraint(constraint));
  }

  seedContextNode(node: ContextNodeRecord): void {
    this.contextNodes.set(node.id, cloneContextNode(node));
  }

  seedDecision(decision: DecisionRecord): void {
    this.decisions.set(decision.id, cloneDecision(decision));
  }

  override replaceTaskLabelsUnlocked(taskId: string, labelIds: string[]): LabelRecord[] {
    const unique: string[] = [];
    const seen = new Set<string>();
    for (const id of labelIds) {
      if (seen.has(id)) {
        continue;
      }
      seen.add(id);
      unique.push(id);
    }
    this.taskLabelIds.set(taskId, unique);
    const result: LabelRecord[] = [];
    for (const id of unique) {
      const label = this.labels.get(id);
      if (label) {
        result.push(cloneLabel(label));
      }
    }
    result.sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    return result;
  }

  insertConstraintUnlocked(constraint: ConstraintRecord): ConstraintRecord {
    if (this.constraints.has(constraint.id)) {
      throw new UniqueViolationError("constraints_pkey");
    }
    this.constraints.set(constraint.id, cloneConstraint(constraint));
    return cloneConstraint(constraint);
  }

  insertDecisionUnlocked(decision: DecisionRecord): DecisionRecord {
    if (this.decisions.has(decision.id)) {
      throw new UniqueViolationError("decisions_pkey");
    }
    const seenPaths = new Set<string>();
    for (const path of decision.relatedPaths) {
      const key = `${path.repoId}:${path.path}`;
      if (seenPaths.has(key)) {
        throw new UniqueViolationError("decision_paths_decision_id_repo_id_path_pk");
      }
      seenPaths.add(key);
    }
    const seenTasks = new Set<string>();
    for (const taskId of decision.relatedTaskIds) {
      if (seenTasks.has(taskId)) {
        throw new UniqueViolationError("decision_tasks_decision_id_task_id_pk");
      }
      seenTasks.add(taskId);
    }
    this.decisions.set(decision.id, cloneDecision(decision));
    return cloneDecision(decision);
  }
  };
}
