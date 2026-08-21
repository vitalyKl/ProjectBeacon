export type { OrgInviteRecord, OrgMemberRecord, OrgRecord, ProjectInviteRecord, ProjectMemberRecord, ProjectRecord } from "../orgs/types.js";
export { InviteTargetRequiredError, OrgSlugTakenError, ProjectSlugTakenError } from "../orgs/types.js";
export { DependencyCycleError, VersionConflictError } from "../roadmap/types.js";
export type { CodeOwnerRecord, ConstraintRecord, ContextNodeRecord, ContextRevisionRecord, DecisionPatch, DecisionRecord, ProjectRepoRecord, DecisionPathLink } from "../context/types.js";
export type { LabelPatch, LabelRecord } from "../labels/types.js";
export type { ActivityEventRecord, MilestonePatch, MilestoneRecord, TaskCommentRecord, TaskDependencyRecord, TaskPatch, TaskRecord } from "../roadmap/types.js";
export type { AgentSessionRef, ApprovalRecord, RateBucketRecord, TokenRecord } from "../tokens/types.js";
export type { AgentSessionRecord, FinishWorkResult, HandoffRecord } from "../sessions/types.js";
export { InvalidReferenceError, SessionNotActiveError, TaskLockedError } from "../sessions/types.js";
export type { SessionRecord, UserRecord } from "./identity.js";
export { BootstrapConsumedError, GithubIdTakenError, LoginTakenError } from "./identity.js";
export type { ProjectRepoRef } from "../repos/store.js";
export { UniqueViolationError, ProjectNotFoundError } from "./errors.js";
export { MemoryAuthStore } from "./memory-store/index.js";

import type { ContextStore } from "../context/store.js";
import type { GithubStore } from "../github/store.js";
import type { JobStore } from "../jobs/store.js";
import type { OrgStore } from "../orgs/store.js";
import type { ReportStore } from "../reports/store.js";
import type { RepoStore } from "../repos/store.js";
import type { RoadmapStore } from "../roadmap/store.js";
import type { WorkStore } from "../sessions/store.js";
import type { TokenStore } from "../tokens/store.js";
import type { IdentityStore } from "./identity.js";

export type AuthStore = IdentityStore &
  OrgStore &
  TokenStore &
  RoadmapStore &
  ContextStore &
  WorkStore &
  RepoStore &
  GithubStore &
  ReportStore &
  JobStore;
