import type { IdempotencyActorType } from "../roadmap/types.js";
import type {
  AgentSessionRecord,
  FinishWorkInput,
  FinishWorkResult,
  HandoffRecord,
  StartWorkInput,
  StartWorkWriteResult,
} from "./types.js";

export interface WorkStore {
  withIdempotency<T>(
    actorType: IdempotencyActorType,
    actorId: string,
    key: string,
    now: Date,
    produce: () => Promise<T>,
  ): Promise<T>;
  startWork(input: StartWorkInput): Promise<StartWorkWriteResult>;
  findAgentSessionById(id: string): Promise<AgentSessionRecord | undefined>;
  listAgentSessions(projectId: string): Promise<AgentSessionRecord[]>;
  heartbeatSession(id: string, now: Date): Promise<AgentSessionRecord | undefined>;
  finishWork(input: FinishWorkInput): Promise<FinishWorkResult | undefined>;
  findLatestHandoffByTaskId(taskId: string): Promise<HandoffRecord | undefined>;
}
