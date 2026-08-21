import type { TokenRecord, ApprovalRecord, RateBucketRecord } from "../../tokens/types.js";
import { cloneToken, cloneApproval, cloneRateBucket } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryTokens<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryTokens extends Base {
  async createApiToken(token: TokenRecord): Promise<TokenRecord> {
    this.apiTokens.set(token.id, cloneToken(token));
    return cloneToken(token);
  }

  async listApiTokens(projectId: string): Promise<TokenRecord[]> {
    const result: TokenRecord[] = [];
    for (const token of this.apiTokens.values()) {
      if (token.projectId === projectId) {
        result.push(cloneToken(token));
      }
    }
    result.sort(
      (a, b) => a.createdAt.getTime() - b.createdAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findApiTokenById(id: string): Promise<TokenRecord | undefined> {
    const token = this.apiTokens.get(id);
    return token ? cloneToken(token) : undefined;
  }

  async findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined> {
    for (const token of this.apiTokens.values()) {
      if (token.tokenHash.equals(tokenHash)) {
        return cloneToken(token);
      }
    }
    return undefined;
  }

  async touchApiToken(id: string, lastUsedAt: Date): Promise<void> {
    const token = this.apiTokens.get(id);
    if (!token) {
      return;
    }
    token.lastUsedAt = new Date(lastUsedAt);
  }

  async revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined> {
    const token = this.apiTokens.get(id);
    if (!token || token.revokedAt) {
      return token ? cloneToken(token) : undefined;
    }
    token.revokedAt = new Date(revokedAt);
    return cloneToken(token);
  }

  async createApproval(approval: ApprovalRecord): Promise<ApprovalRecord> {
    this.approvals.set(approval.id, cloneApproval(approval));
    return cloneApproval(approval);
  }

  async listApprovals(
    projectId: string,
    status?: ApprovalRecord["status"],
  ): Promise<ApprovalRecord[]> {
    const result: ApprovalRecord[] = [];
    for (const approval of this.approvals.values()) {
      if (approval.projectId !== projectId) {
        continue;
      }
      if (status && approval.status !== status) {
        continue;
      }
      result.push(cloneApproval(approval));
    }
    result.sort(
      (a, b) => a.requestedAt.getTime() - b.requestedAt.getTime() || a.id.localeCompare(b.id),
    );
    return result;
  }

  async findApprovalById(id: string): Promise<ApprovalRecord | undefined> {
    const approval = this.approvals.get(id);
    return approval ? cloneApproval(approval) : undefined;
  }

  async resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined> {
    const approval = this.approvals.get(id);
    if (!approval || approval.status !== "pending") {
      return undefined;
    }
    approval.status = decision;
    approval.resolvedAt = new Date(resolvedAt);
    approval.resolvedBy = resolvedBy;
    return cloneApproval(approval);
  }

  async consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord> {
    const existing = this.rateBuckets.get(input.bucketKey);
    if (!existing || existing.windowStart.getTime() !== input.windowStart.getTime()) {
      const created: RateBucketRecord = {
        bucketKey: input.bucketKey,
        windowStart: new Date(input.windowStart),
        count: input.countDelta,
        bytes: BigInt(input.bytesDelta),
      };
      this.rateBuckets.set(input.bucketKey, created);
      return cloneRateBucket(created);
    }
    existing.count += input.countDelta;
    existing.bytes += BigInt(input.bytesDelta);
    return cloneRateBucket(existing);
  }
  };
}
