import type { ConstraintRecord, DecisionRecord } from "../context/types.js";
import type { LabelRecord } from "../labels/types.js";
import type {
  ActivityEventRecord,
  IdempotencyActorType,
  MilestoneRecord,
  TaskCommentRecord,
  TaskRecord,
} from "../roadmap/types.js";
import type {
  AgentSessionRecord,
  FinishWorkInput,
  FinishWorkResult,
  HandoffRecord,
  StartWorkInput,
  StartWorkWriteResult,
} from "./types.js";

export type IdempotentWrites = {
  createTask(task: TaskRecord): Promise<TaskRecord>;
  createComment(comment: TaskCommentRecord): Promise<TaskCommentRecord>;
  writeActivity(event: ActivityEventRecord): Promise<ActivityEventRecord>;
  startWork(input: StartWorkInput): Promise<StartWorkWriteResult>;
  createDecision(decision: DecisionRecord): Promise<DecisionRecord>;
  createConstraint(constraint: ConstraintRecord): Promise<ConstraintRecord>;
  createMilestone?(milestone: MilestoneRecord): Promise<MilestoneRecord>;
  setTaskLabels?(taskId: string, labelIds: string[]): Promise<LabelRecord[]>;
};

export interface WorkStore {
  withIdempotency(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: (writes: IdempotentWrites) => Promise<unknown>,
  ): Promise<unknown>;
  findAgentSessionById(id: string): Promise<AgentSessionRecord | undefined>;
  listAgentSessions(projectId: string): Promise<AgentSessionRecord[]>;
  heartbeatSession(id: string, now: Date): Promise<AgentSessionRecord | undefined>;
  finishWork(input: FinishWorkInput): Promise<FinishWorkResult | undefined>;
  findLatestHandoffByTaskId(taskId: string): Promise<HandoffRecord | undefined>;
}
