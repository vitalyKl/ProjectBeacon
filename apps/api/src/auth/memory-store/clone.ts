import type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionRecord, ProjectRepoRecord } from "../../context/types.js";
import type { GithubInstallationRecord, GithubSyncStateRecord } from "../../github/types.js";
import type { LabelRecord } from "../../labels/types.js";
import type { OrgInviteRecord, OrgMemberRecord, OrgRecord, ProjectInviteRecord, ProjectMemberRecord, ProjectRecord } from "../../orgs/types.js";
import type { ActivityEventRecord, IdempotencyActorType, MilestoneRecord, TaskCommentRecord, TaskRecord } from "../../roadmap/types.js";
import type { AgentSessionRecord, HandoffRecord } from "../../sessions/types.js";
import type { ApprovalRecord, RateBucketRecord, TokenRecord } from "../../tokens/types.js";
import type { SessionRecord, UserRecord } from "../identity.js";

export function cloneUser(user: UserRecord): UserRecord {
  return { ...user };
}

export function cloneSession(session: SessionRecord): SessionRecord {
  return {
    ...session,
    tokenHash: Buffer.from(session.tokenHash),
    createdAt: new Date(session.createdAt),
    lastSeenAt: new Date(session.lastSeenAt),
    expiresAt: new Date(session.expiresAt),
    revokedAt: session.revokedAt ? new Date(session.revokedAt) : null,
  };
}

export function cloneOrg(org: OrgRecord): OrgRecord {
  return { ...org, createdAt: new Date(org.createdAt) };
}

export function cloneOrgMember(member: OrgMemberRecord): OrgMemberRecord {
  return { ...member };
}

export function cloneOrgInvite(invite: OrgInviteRecord): OrgInviteRecord {
  return {
    ...invite,
    expiresAt: new Date(invite.expiresAt),
    acceptedAt: invite.acceptedAt ? new Date(invite.acceptedAt) : null,
  };
}

export function cloneProject(project: ProjectRecord): ProjectRecord {
  return {
    ...project,
    settings: { ...project.settings },
    createdAt: new Date(project.createdAt),
    updatedAt: new Date(project.updatedAt),
    deletedAt: project.deletedAt ? new Date(project.deletedAt) : null,
  };
}

export function cloneProjectMember(member: ProjectMemberRecord): ProjectMemberRecord {
  return { ...member, createdAt: new Date(member.createdAt) };
}

export function cloneProjectInvite(invite: ProjectInviteRecord): ProjectInviteRecord {
  return {
    ...invite,
    expiresAt: new Date(invite.expiresAt),
    acceptedAt: invite.acceptedAt ? new Date(invite.acceptedAt) : null,
  };
}

export function cloneToken(token: TokenRecord): TokenRecord {
  return {
    ...token,
    tokenHash: Buffer.from(token.tokenHash),
    scopes: [...token.scopes],
    lastUsedAt: token.lastUsedAt ? new Date(token.lastUsedAt) : null,
    expiresAt: token.expiresAt ? new Date(token.expiresAt) : null,
    revokedAt: token.revokedAt ? new Date(token.revokedAt) : null,
    createdAt: new Date(token.createdAt),
  };
}

export function cloneApproval(approval: ApprovalRecord): ApprovalRecord {
  return {
    ...approval,
    payload: { ...approval.payload },
    requestedAt: new Date(approval.requestedAt),
    resolvedAt: approval.resolvedAt ? new Date(approval.resolvedAt) : null,
  };
}

export function cloneRateBucket(bucket: RateBucketRecord): RateBucketRecord {
  return {
    ...bucket,
    windowStart: new Date(bucket.windowStart),
    bytes: bucket.bytes,
  };
}

export function emailsEqual(left: string | null | undefined, right: string | null | undefined): boolean {
  if (!left || !right) {
    return false;
  }
  return left.toLowerCase() === right.toLowerCase();
}
export function cloneLabel(label: LabelRecord): LabelRecord {
  return {
    ...label,
    paths: label.paths.map((path) => ({ ...path })),
    createdAt: new Date(label.createdAt),
  };
}

export function cloneMilestone(milestone: MilestoneRecord): MilestoneRecord {
  return { ...milestone, createdAt: new Date(milestone.createdAt) };
}

export function cloneTask(task: TaskRecord): TaskRecord {
  return {
    ...task,
    linkedPaths: task.linkedPaths.map((path) => ({ ...path })),
    lockExpiresAt: task.lockExpiresAt ? new Date(task.lockExpiresAt) : null,
    deletedAt: task.deletedAt ? new Date(task.deletedAt) : null,
    createdAt: new Date(task.createdAt),
    updatedAt: new Date(task.updatedAt),
  };
}

export function cloneComment(comment: TaskCommentRecord): TaskCommentRecord {
  return { ...comment, createdAt: new Date(comment.createdAt) };
}

export function cloneActivity(event: ActivityEventRecord): ActivityEventRecord {
  return {
    ...event,
    payload: { ...event.payload },
    createdAt: new Date(event.createdAt),
  };
}

export function cloneContextNode(node: ContextNodeRecord): ContextNodeRecord {
  return {
    ...node,
    sections: node.sections.map((section) => ({ ...section })),
    updatedAt: new Date(node.updatedAt),
  };
}

export function cloneConstraint(constraint: ConstraintRecord): ConstraintRecord {
  return { ...constraint, createdAt: new Date(constraint.createdAt) };
}

export function cloneDecision(decision: DecisionRecord): DecisionRecord {
  return {
    ...decision,
    relatedPaths: decision.relatedPaths.map((path) => ({ ...path })),
    relatedTaskIds: [...decision.relatedTaskIds],
    createdAt: new Date(decision.createdAt),
  };
}

export function cloneContextRevision(revision: ContextRevisionRecord): ContextRevisionRecord {
  return {
    ...revision,
    target: { ...revision.target },
    briefJson: structuredClone(revision.briefJson),
    sourceNodeIds: [...revision.sourceNodeIds],
    createdAt: new Date(revision.createdAt),
  };
}

export function cloneProjectRepo(repo: ProjectRepoRecord): ProjectRepoRecord {
  return {
    ...repo,
    lastIndexedAt: repo.lastIndexedAt ? new Date(repo.lastIndexedAt) : null,
  };
}

export function cloneCodeOwner(row: CodeOwnerRecord): CodeOwnerRecord {
  return { ...row, owners: [...row.owners] };
}

export function idempotencyKey(actorType: IdempotencyActorType, actorId: string, key: string): string {
  return `${actorType}:${actorId}:${key}`;
}

export function cloneAgentSession(session: AgentSessionRecord): AgentSessionRecord {
  return {
    ...session,
    startedAt: new Date(session.startedAt),
    finishedAt: session.finishedAt ? new Date(session.finishedAt) : null,
    lockExpiresAt: session.lockExpiresAt ? new Date(session.lockExpiresAt) : null,
    lastHeartbeatAt: new Date(session.lastHeartbeatAt),
  };
}

export function cloneHandoff(handoff: HandoffRecord): HandoffRecord {
  return {
    ...handoff,
    filesTouched: handoff.filesTouched.map((path) => ({ ...path })),
    openQuestions: [...handoff.openQuestions],
    createdAt: new Date(handoff.createdAt),
  };
}

export function cloneGithubInstallation(row: GithubInstallationRecord): GithubInstallationRecord {
  return { ...row, createdAt: new Date(row.createdAt) };
}

export function cloneGithubSyncState(row: GithubSyncStateRecord): GithubSyncStateRecord {
  return {
    ...row,
    lastSyncedAt: row.lastSyncedAt ? new Date(row.lastSyncedAt) : null,
  };
}

export function cloneSidecar(row: {
  id: string;
  repoId: string;
  tokenId: string;
  connectedAt: Date;
  lastSeenAt: Date;
}): { id: string; repoId: string; tokenId: string; connectedAt: Date; lastSeenAt: Date } {
  return {
    ...row,
    connectedAt: new Date(row.connectedAt),
    lastSeenAt: new Date(row.lastSeenAt),
  };
}