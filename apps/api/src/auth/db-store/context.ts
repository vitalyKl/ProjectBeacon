import { constraints, contextNodes, contextRevisions, decisionPaths, decisions, labelPaths, labels, taskLabels, decisionTasks } from "@beacon/db";
import { and, asc, desc, eq, inArray, isNull } from "drizzle-orm";
import { UniqueViolationError } from "../errors.js";
import type { Db } from "@beacon/db";
import type { LinkedPath } from "../../roadmap/types.js";
import type { LabelRecord, LabelPatch } from "../../labels/types.js";
import type { ContextNodeRecord, ConstraintRecord, DecisionRecord, DecisionPatch, ContextRevisionRecord, DecisionPathLink } from "../../context/types.js";
import type { ImportContextInput, ImportContextResult } from "../../context/store.js";
import { uniqueIds, uniqueConstraint, toContextNode, upsertContextNodeInDb, upsertCodeOwnersInTx, toConstraint, toLabel, toDecision, toContextRevision, insertDecisionTx } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { ContextStore } from "../../context/store.js";

export function withDbContext<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<ContextStore> {
  return class DbContext extends Base {
  async listContextNodes(projectId: string): Promise<ContextNodeRecord[]> {
    const rows = await this.db
      .select()
      .from(contextNodes)
      .where(eq(contextNodes.projectId, projectId))
      .orderBy(asc(contextNodes.id));
    return rows.flatMap((row) => {
      const node = toContextNode(row);
      return node ? [node] : [];
    });
  }

  async upsertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    return upsertContextNodeInDb(this.writeDb(), node);
  }

  async importContext(input: ImportContextInput): Promise<ImportContextResult> {
    const run = async (tx: Db): Promise<ImportContextResult> => {
      const nodes: ContextNodeRecord[] = [];
      for (const node of input.nodes) {
        nodes.push(await upsertContextNodeInDb(tx, node));
      }
      let codeOwnersWritten = 0;
      if (input.codeOwners) {
        const written = await upsertCodeOwnersInTx(
          tx,
          input.codeOwners.repoId,
          input.codeOwners.rows,
        );
        codeOwnersWritten = written.length;
      }
      return { nodes, codeOwnersWritten };
    };
    const bound = this.writeTx.getStore();
    if (bound) {
      return run(bound);
    }
    return this.db.transaction((tx) => run(tx as unknown as Db));
  }

  async listActiveConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const rows = await this.db
      .select()
      .from(constraints)
      .where(and(eq(constraints.projectId, projectId), eq(constraints.status, "active")))
      .orderBy(asc(constraints.id));
    return rows.flatMap((row) => {
      const constraint = toConstraint(row);
      return constraint ? [constraint] : [];
    });
  }

  async listConstraints(projectId: string): Promise<ConstraintRecord[]> {
    const rows = await this.db
      .select()
      .from(constraints)
      .where(eq(constraints.projectId, projectId))
      .orderBy(desc(constraints.createdAt), desc(constraints.id));
    return rows.flatMap((row) => {
      const constraint = toConstraint(row);
      return constraint ? [constraint] : [];
    });
  }

  async insertConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    return this.createConstraint(constraint);
  }

  async listAcceptedDecisions(projectId: string): Promise<DecisionRecord[]> {
    const rows = await this.db
      .select()
      .from(decisions)
      .where(and(eq(decisions.projectId, projectId), eq(decisions.status, "accepted")))
      .orderBy(asc(decisions.id));
    return this.attachDecisionLinks(rows);
  }

  async insertContextRevision(revision: ContextRevisionRecord): Promise<ContextRevisionRecord> {
    const [row] = await this.db
      .insert(contextRevisions)
      .values({
        id: revision.id,
        projectId: revision.projectId,
        compiledHash: revision.compiledHash,
        compilerVersion: revision.compilerVersion,
        target: revision.target,
        briefMarkdown: revision.briefMarkdown,
        briefJson: revision.briefJson,
        tokenEstimate: revision.tokenEstimate,
        sourceNodeIds: revision.sourceNodeIds,
        sessionId: revision.sessionId,
        createdAt: revision.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert context revision returned no row");
    }
    return toContextRevision(row);
  }

  async listContextRevisions(projectId: string): Promise<ContextRevisionRecord[]> {
    const rows = await this.db
      .select()
      .from(contextRevisions)
      .where(eq(contextRevisions.projectId, projectId))
      .orderBy(desc(contextRevisions.createdAt), desc(contextRevisions.id));
    return rows.map(toContextRevision);
  }

  async findContextRevisionById(id: string): Promise<ContextRevisionRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(contextRevisions)
      .where(eq(contextRevisions.id, id))
      .limit(1);
    return row ? toContextRevision(row) : undefined;
  }

  async findConstraintById(id: string): Promise<ConstraintRecord | undefined> {
    const [row] = await this.db.select().from(constraints).where(eq(constraints.id, id)).limit(1);
    return row ? toConstraint(row) : undefined;
  }

  async createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord> {
    const [row] = await this.writeDb()
      .insert(constraints)
      .values({
        id: constraint.id,
        projectId: constraint.projectId,
        kind: constraint.kind,
        body: constraint.body,
        scopePath: constraint.scopePath,
        status: constraint.status,
        createdAt: constraint.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert constraint returned no row");
    }
    const created = toConstraint(row);
    if (!created) {
      throw new Error("insert constraint returned invalid row");
    }
    return created;
  }

  async applyConstraint(id: string, appliedAt: Date): Promise<ConstraintRecord | undefined> {
    void appliedAt;
    const [row] = await this.db
      .update(constraints)
      .set({ status: "active" })
      .where(and(eq(constraints.id, id), eq(constraints.status, "proposed")))
      .returning();
    return row ? toConstraint(row) : undefined;
  }

  async listDecisions(projectId: string): Promise<DecisionRecord[]> {
    const rows = await this.db
      .select()
      .from(decisions)
      .where(eq(decisions.projectId, projectId))
      .orderBy(desc(decisions.createdAt), desc(decisions.id));
    return this.attachDecisionLinks(rows);
  }

  async findDecisionById(id: string): Promise<DecisionRecord | undefined> {
    const [row] = await this.db.select().from(decisions).where(eq(decisions.id, id)).limit(1);
    if (!row) {
      return undefined;
    }
    const [linked] = await this.attachDecisionLinks([row]);
    return linked;
  }

  async createDecision(decision: DecisionRecord): Promise<DecisionRecord> {
    const bound = this.writeTx.getStore();
    if (bound) {
      return insertDecisionTx(bound, decision);
    }
    return this.db.transaction((tx) => insertDecisionTx(tx, decision));
  }

  async updateDecision(id: string, patch: DecisionPatch): Promise<DecisionRecord | undefined> {
    const [row] = await this.db
      .update(decisions)
      .set({
        status: patch.status,
        ...(patch.supersededBy !== undefined ? { supersededBy: patch.supersededBy } : {}),
      })
      .where(eq(decisions.id, id))
      .returning();
    if (!row) {
      return undefined;
    }
    const [linked] = await this.attachDecisionLinks([row]);
    return linked;
  }

  async listLabels(projectId: string): Promise<LabelRecord[]> {
    const rows = await this.db
      .select()
      .from(labels)
      .where(eq(labels.projectId, projectId))
      .orderBy(asc(labels.status), asc(labels.name), asc(labels.id));
    return this.attachLabelPaths(rows);
  }

  async findLabelById(id: string): Promise<LabelRecord | undefined> {
    const [row] = await this.db.select().from(labels).where(eq(labels.id, id)).limit(1);
    if (!row) {
      return undefined;
    }
    const [linked] = await this.attachLabelPaths([row]);
    return linked;
  }

  async createLabel(label: LabelRecord): Promise<LabelRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        const [row] = await tx
          .insert(labels)
          .values({
            id: label.id,
            projectId: label.projectId,
            slug: label.slug,
            name: label.name,
            description: label.description,
            color: label.color,
            status: label.status,
            createdAt: label.createdAt,
          })
          .returning();
        if (!row) {
          throw new Error("insert label returned no row");
        }
        if (label.paths.length > 0) {
          await tx.insert(labelPaths).values(
            label.paths.map((path) => ({
              labelId: label.id,
              repoId: path.repo_id,
              path: path.path,
            })),
          );
        }
        return toLabel(row, label.paths);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "label_slug") {
        throw new UniqueViolationError("labels_project_slug");
      }
      throw error;
    }
  }

  async updateLabel(id: string, patch: LabelPatch): Promise<LabelRecord | undefined> {
    try {
      return await this.db.transaction(async (tx) => {
        const [current] = await tx.select().from(labels).where(eq(labels.id, id)).limit(1);
        if (!current) {
          return undefined;
        }
        const [row] = await tx
          .update(labels)
          .set({
            ...(patch.name !== undefined ? { name: patch.name } : {}),
            ...(patch.slug !== undefined ? { slug: patch.slug } : {}),
            ...(patch.description !== undefined ? { description: patch.description } : {}),
            ...(patch.color !== undefined ? { color: patch.color } : {}),
            ...(patch.status !== undefined ? { status: patch.status } : {}),
          })
          .where(eq(labels.id, id))
          .returning();
        if (!row) {
          return undefined;
        }
        if (patch.paths !== undefined) {
          await tx.delete(labelPaths).where(eq(labelPaths.labelId, id));
          if (patch.paths.length > 0) {
            await tx.insert(labelPaths).values(
              patch.paths.map((path) => ({
                labelId: id,
                repoId: path.repo_id,
                path: path.path,
              })),
            );
          }
        }
        const paths =
          patch.paths ??
          (await tx.select().from(labelPaths).where(eq(labelPaths.labelId, id))).map((item) => ({
            repo_id: item.repoId,
            path: item.path,
          }));
        return toLabel(row, paths);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "label_slug") {
        throw new UniqueViolationError("labels_project_slug");
      }
      throw error;
    }
  }

  async listTaskLabels(taskId: string): Promise<LabelRecord[]> {
    const db = this.writeDb();
    const links = await db.select().from(taskLabels).where(eq(taskLabels.taskId, taskId));
    if (links.length === 0) {
      return [];
    }
    const rows = await db
      .select()
      .from(labels)
      .where(
        inArray(
          labels.id,
          links.map((link) => link.labelId),
        ),
      )
      .orderBy(asc(labels.name), asc(labels.id));
    return this.attachLabelPaths(rows);
  }

  async setTaskLabels(taskId: string, labelIds: string[]): Promise<LabelRecord[]> {
    const unique = uniqueIds(labelIds);
    const apply = async (db: Db) => {
      await db.delete(taskLabels).where(eq(taskLabels.taskId, taskId));
      if (unique.length > 0) {
        await db.insert(taskLabels).values(unique.map((labelId) => ({ taskId, labelId })));
      }
    };
    const bound = this.writeTx.getStore();
    if (bound) {
      await apply(bound);
    } else {
      await this.db.transaction((tx) => apply(tx as unknown as Db));
    }
    return this.listTaskLabels(taskId);
  }

  async attachLabelPaths(rows: (typeof labels.$inferSelect)[]): Promise<LabelRecord[]> {
    if (rows.length === 0) {
      return [];
    }
    const paths = await this.writeDb()
      .select()
      .from(labelPaths)
      .where(
        inArray(
          labelPaths.labelId,
          rows.map((row) => row.id),
        ),
      );
    const byLabel = new Map<string, LinkedPath[]>();
    for (const path of paths) {
      const list = byLabel.get(path.labelId) ?? [];
      list.push({ repo_id: path.repoId, path: path.path });
      byLabel.set(path.labelId, list);
    }
    return rows.map((row) => toLabel(row, byLabel.get(row.id) ?? []));
  }

  async findContextNodeById(id: string): Promise<ContextNodeRecord | undefined> {
    const [row] = await this.db.select().from(contextNodes).where(eq(contextNodes.id, id)).limit(1);
    return row ? toContextNode(row) : undefined;
  }

  async findContextNodeByScope(scope: {
    projectId: string;
    scopeType: ContextNodeRecord["scopeType"];
    repoId: string | null;
    path: string;
    taskId: string | null;
  }): Promise<ContextNodeRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(contextNodes)
      .where(
        and(
          eq(contextNodes.projectId, scope.projectId),
          eq(contextNodes.scopeType, scope.scopeType),
          eq(contextNodes.path, scope.path),
          scope.repoId === null
            ? isNull(contextNodes.repoId)
            : eq(contextNodes.repoId, scope.repoId),
          scope.taskId === null
            ? isNull(contextNodes.taskId)
            : eq(contextNodes.taskId, scope.taskId),
        ),
      )
      .limit(1);
    return row ? toContextNode(row) : undefined;
  }

  async insertContextNode(node: ContextNodeRecord): Promise<ContextNodeRecord> {
    const existing = await this.findContextNodeByScope(node);
    if (existing) {
      return existing;
    }
    try {
      const [row] = await this.db
        .insert(contextNodes)
        .values({
          id: node.id,
          projectId: node.projectId,
          repoId: node.repoId,
          taskId: node.taskId,
          scopeType: node.scopeType,
          path: node.path,
          sections: node.sections,
          sectionsText: node.sectionsText,
          source: node.source,
          sourcePath: node.sourcePath,
          reviewState: node.reviewState,
          updatedByType: node.updatedByType,
          updatedById: node.updatedById,
          updatedAt: node.updatedAt,
        })
        .returning();
      if (!row) {
        throw new Error("insert context node returned no row");
      }
      const stored = toContextNode(row);
      if (!stored) {
        throw new Error("insert context node returned invalid row");
      }
      return stored;
    } catch (error) {
      if (uniqueConstraint(error) !== "context_node_scope") {
        throw error;
      }
      const raced = await this.findContextNodeByScope(node);
      if (!raced) {
        throw error;
      }
      return raced;
    }
  }

  async attachDecisionLinks(
    rows: (typeof decisions.$inferSelect)[],
  ): Promise<DecisionRecord[]> {
    if (rows.length === 0) {
      return [];
    }
    const ids = rows.map((row) => row.id);
    const [paths, taskLinks] = await Promise.all([
      this.db.select().from(decisionPaths).where(inArray(decisionPaths.decisionId, ids)),
      this.db.select().from(decisionTasks).where(inArray(decisionTasks.decisionId, ids)),
    ]);
    const pathsByDecision = new Map<string, DecisionPathLink[]>();
    for (const path of paths) {
      const list = pathsByDecision.get(path.decisionId) ?? [];
      list.push({ repoId: path.repoId, path: path.path });
      pathsByDecision.set(path.decisionId, list);
    }
    const tasksByDecision = new Map<string, string[]>();
    for (const link of taskLinks) {
      const list = tasksByDecision.get(link.decisionId) ?? [];
      list.push(link.taskId);
      tasksByDecision.set(link.decisionId, list);
    }
    return rows.flatMap((row) => {
      const decision = toDecision(
        row,
        pathsByDecision.get(row.id) ?? [],
        tasksByDecision.get(row.id) ?? [],
      );
      return decision ? [decision] : [];
    });
  }
  } as TBase & Ctor<ContextStore>;
}
