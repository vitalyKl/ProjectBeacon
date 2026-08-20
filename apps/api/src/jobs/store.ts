import type { SidecarConnectionRecord } from "../repos/store.js";
import type { ExpireLocksCounts, RetentionCounts } from "./policy.js";

export interface JobStore {
  runRetention(now: Date): Promise<RetentionCounts>;
  expireLocks(now: Date): Promise<ExpireLocksCounts>;
  listSidecarConnections(): Promise<SidecarConnectionRecord[]>;
  countPendingApprovals(): Promise<number>;
  githubSyncLagSeconds(now: Date): Promise<number>;
  backfillEmptyProjectLabelCatalogs(): Promise<number>;
}
