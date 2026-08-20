import type { ApprovalRecord, RateBucketRecord, TokenRecord } from "./types.js";

export interface TokenStore {
  createApiToken(token: TokenRecord): Promise<TokenRecord>;
  listApiTokens(projectId: string): Promise<TokenRecord[]>;
  findApiTokenById(id: string): Promise<TokenRecord | undefined>;
  findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined>;
  touchApiToken(id: string, lastUsedAt: Date): Promise<void>;
  revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined>;
  createApproval(approval: ApprovalRecord): Promise<ApprovalRecord>;
  listApprovals(projectId: string, status?: ApprovalRecord["status"]): Promise<ApprovalRecord[]>;
  findApprovalById(id: string): Promise<ApprovalRecord | undefined>;
  resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined>;
  consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord>;
}
