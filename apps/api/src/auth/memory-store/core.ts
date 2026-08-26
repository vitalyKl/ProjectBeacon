import { AsyncLocalStorage } from "node:async_hooks";

import {
  DEFAULT_PROJECT_LABELS,
  DEFAULT_PROJECT_LABEL_STATUS,
  DEFAULT_SECURITY_CONSTRAINTS,
  DEFAULT_SECURITY_CONSTRAINT_KIND,
  DEFAULT_SECURITY_CONSTRAINT_STATUS,
} from "@beacon/context";
import { uuidv7 } from "@beacon/shared";
import type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionRecord, ProjectRepoRecord } from "../../context/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../../github/types.js";
import type { LabelRecord } from "../../labels/types.js";
import type {
  OrgInviteRecord,
  OrgMemberRecord,
  OrgRecord,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
} from "../../orgs/types.js";
import type { ProjectEvalMetricRecord, ProjectReportRecord, ProjectReviewRecord } from "../../reports/types.js";
import {
  IDEMPOTENCY_TTL_MS,
  type ActivityEventRecord,
  type IdempotencyActorType,
  type MilestoneRecord,
  type TaskCommentRecord,
  type TaskDependencyRecord,
  type TaskRecord,
} from "../../roadmap/types.js";
import type { AgentSessionRecord, HandoffRecord } from "../../sessions/types.js";
import type { ApprovalRecord, RateBucketRecord, TokenRecord } from "../../tokens/types.js";
import type { SessionRecord, UserRecord } from "../identity.js";
import { cloneActivity, cloneCodeOwner, cloneLabel, idempotencyKey } from "./clone.js";

export class MemoryStoreCore {
  readonly users = new Map<string, UserRecord>();
  readonly sessions = new Map<string, SessionRecord>();
  readonly orgs = new Map<string, OrgRecord>();
  readonly orgMembers = new Map<string, OrgMemberRecord>();
  readonly orgInvites = new Map<string, OrgInviteRecord>();
  readonly projects = new Map<string, ProjectRecord>();
  readonly projectMembers = new Map<string, ProjectMemberRecord>();
  readonly projectInvites = new Map<string, ProjectInviteRecord>();
  readonly apiTokens = new Map<string, TokenRecord>();
  readonly approvals = new Map<string, ApprovalRecord>();
  readonly rateBuckets = new Map<string, RateBucketRecord>();
  readonly milestones = new Map<string, MilestoneRecord>();
  readonly tasks = new Map<string, TaskRecord>();
  readonly comments = new Map<string, TaskCommentRecord>();
  readonly dependencies: TaskDependencyRecord[] = [];
  readonly activity = new Map<string, ActivityEventRecord>();
  readonly contextNodes = new Map<string, ContextNodeRecord>();
  readonly constraints = new Map<string, ConstraintRecord>();
  readonly decisions = new Map<string, DecisionRecord>();
  readonly labels = new Map<string, LabelRecord>();
  readonly taskLabelIds = new Map<string, string[]>();
  readonly contextRevisions = new Map<string, ContextRevisionRecord>();
  readonly projectRepos = new Map<string, ProjectRepoRecord>();
  readonly codeOwners = new Map<string, CodeOwnerRecord>();
  readonly idempotency = new Map<string, { response: unknown; createdAt: Date }>();
  readonly agentSessions = new Map<string, AgentSessionRecord>();
  readonly handoffs = new Map<string, HandoffRecord>();
  readonly githubInstallations = new Map<string, GithubInstallationRecord>();
  readonly githubSyncState = new Map<string, GithubSyncStateRecord>();
  readonly sidecarConnections = new Map<
    string,
    { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date }
  >();
  readonly cloneInvalidations = new Map<
    string,
    { id: string; repoId: string; sha: string | null; createdAt: Date; consumedAt: Date | null }
  >();
  readonly reports = new Map<string, ProjectReportRecord>();
  readonly reviews = new Map<string, ProjectReviewRecord>();
  readonly evalMetrics = new Map<string, ProjectEvalMetricRecord>();
  writeTail: Promise<void> = Promise.resolve();
  readonly writeContext = new AsyncLocalStorage<true>();

  enqueueWrite<T>(fn: () => T | Promise<T>): Promise<T> {
    if (this.writeContext.getStore()) {
      return Promise.resolve().then(fn);
    }
    const run = this.writeTail.then(() => this.writeContext.run(true, fn));
    this.writeTail = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  async withIdempotency<T>(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: () => Promise<T>,
  ): Promise<T> {
    return this.enqueueWrite(async () => {
      const slot = idempotencyKey(actorType, actorId, key);
      const stored = this.idempotency.get(slot);
      if (stored && now.getTime() - stored.createdAt.getTime() <= IDEMPOTENCY_TTL_MS) {
        return structuredClone(stored.response) as T;
      }
      this.idempotency.delete(slot);
      const response = await produce();
      this.idempotency.set(slot, {
        response: structuredClone(response),
        createdAt: new Date(now),
      });
      return structuredClone(response);
    });
  }

  seedDefaultSecurityConstraints(projectId: string, createdAt: Date): void {
    const existing = [...this.constraints.values()].filter(
      (constraint) =>
        constraint.projectId === projectId &&
        constraint.kind === DEFAULT_SECURITY_CONSTRAINT_KIND &&
        constraint.status === DEFAULT_SECURITY_CONSTRAINT_STATUS,
    );
    const existingBodies = new Set(existing.map((constraint) => constraint.body));
    let offset = 0;
    for (const body of DEFAULT_SECURITY_CONSTRAINTS) {
      if (existingBodies.has(body)) {
        continue;
      }
      const id = uuidv7(createdAt.getTime() + offset);
      offset += 1;
      this.constraints.set(id, {
        id,
        projectId,
        kind: DEFAULT_SECURITY_CONSTRAINT_KIND,
        body,
        scopePath: "",
        status: DEFAULT_SECURITY_CONSTRAINT_STATUS,
        createdAt: new Date(createdAt),
      });
    }
  }

  seedDefaultProjectLabels(projectId: string, createdAt: Date): void {
    const existingSlugs = new Set(
      [...this.labels.values()]
        .filter((label) => label.projectId === projectId)
        .map((label) => label.slug),
    );
    let offset = 0;
    for (const seed of DEFAULT_PROJECT_LABELS) {
      if (existingSlugs.has(seed.slug)) {
        continue;
      }
      const id = uuidv7(createdAt.getTime() + offset);
      offset += 1;
      this.labels.set(id, {
        id,
        projectId,
        slug: seed.slug,
        name: seed.name,
        description: seed.description,
        color: seed.color,
        status: DEFAULT_PROJECT_LABEL_STATUS,
        createdAt: new Date(createdAt),
        paths: [],
      });
    }
  }

  insertActivityUnlocked(event: ActivityEventRecord): ActivityEventRecord {
    this.activity.set(event.id, cloneActivity(event));
    return cloneActivity(event);
  }

  replaceTaskLabelsUnlocked(taskId: string, labelIds: string[]): LabelRecord[] {
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

  upsertCodeOwnersUnlocked(repoId: string, rows: CodeOwnerRecord[]): CodeOwnerRecord[] {
    for (const [id, existing] of this.codeOwners) {
      if (existing.repoId === repoId && existing.source === "codeowners") {
        this.codeOwners.delete(id);
      }
    }
    const written: CodeOwnerRecord[] = [];
    const byPattern = new Map<string, CodeOwnerRecord>();
    for (const row of rows) {
      byPattern.set(row.pathPattern, row);
    }
    for (const row of byPattern.values()) {
      const stored = cloneCodeOwner({ ...row, repoId });
      this.codeOwners.set(stored.id, stored);
      written.push(cloneCodeOwner(stored));
    }
    written.sort((a, b) => a.pathPattern.localeCompare(b.pathPattern) || a.id.localeCompare(b.id));
    return written;
  }
}
