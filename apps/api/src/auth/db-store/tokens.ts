import { apiTokens, approvalRequests, rateBuckets } from "@beacon/db";
import { and, asc, eq, isNull, sql } from "drizzle-orm";
import type { TokenRecord, ApprovalRecord, RateBucketRecord } from "../../tokens/types.js";
import { toToken, toApproval, toRateBucket } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { TokenStore } from "../../tokens/store.js";

export function withDbTokens<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<TokenStore> {
  return class DbTokens extends Base {
  async createApiToken(token: TokenRecord): Promise<TokenRecord> {
    const [row] = await this.db
      .insert(apiTokens)
      .values({
        id: token.id,
        projectId: token.projectId,
        name: token.name,
        tokenHash: token.tokenHash,
        prefix: token.prefix,
        scopes: token.scopes,
        createdBy: token.createdBy,
        lastUsedAt: token.lastUsedAt,
        expiresAt: token.expiresAt,
        revokedAt: token.revokedAt,
        createdAt: token.createdAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert api token returned no row");
    }
    return toToken(row);
  }

  async listApiTokens(projectId: string): Promise<TokenRecord[]> {
    const rows = await this.db
      .select()
      .from(apiTokens)
      .where(eq(apiTokens.projectId, projectId))
      .orderBy(asc(apiTokens.createdAt), asc(apiTokens.id));
    return rows.map(toToken);
  }

  async findApiTokenById(id: string): Promise<TokenRecord | undefined> {
    const [row] = await this.db.select().from(apiTokens).where(eq(apiTokens.id, id)).limit(1);
    return row ? toToken(row) : undefined;
  }

  async findApiTokenByHash(tokenHash: Buffer): Promise<TokenRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(apiTokens)
      .where(eq(apiTokens.tokenHash, tokenHash))
      .limit(1);
    return row ? toToken(row) : undefined;
  }

  async touchApiToken(id: string, lastUsedAt: Date): Promise<void> {
    await this.db.update(apiTokens).set({ lastUsedAt }).where(eq(apiTokens.id, id));
  }

  async revokeApiToken(id: string, revokedAt: Date): Promise<TokenRecord | undefined> {
    const [row] = await this.db
      .update(apiTokens)
      .set({ revokedAt })
      .where(and(eq(apiTokens.id, id), isNull(apiTokens.revokedAt)))
      .returning();
    if (row) {
      return toToken(row);
    }
    const existing = await this.findApiTokenById(id);
    return existing;
  }

  async createApproval(approval: ApprovalRecord): Promise<ApprovalRecord> {
    const [row] = await this.db
      .insert(approvalRequests)
      .values({
        id: approval.id,
        projectId: approval.projectId,
        sessionId: approval.sessionId,
        action: approval.action,
        payload: approval.payload,
        status: approval.status,
        requestedAt: approval.requestedAt,
        resolvedAt: approval.resolvedAt,
        resolvedBy: approval.resolvedBy,
      })
      .returning();
    if (!row) {
      throw new Error("insert approval returned no row");
    }
    return toApproval(row);
  }

  async listApprovals(
    projectId: string,
    status?: ApprovalRecord["status"],
  ): Promise<ApprovalRecord[]> {
    const rows = await this.db
      .select()
      .from(approvalRequests)
      .where(
        status
          ? and(eq(approvalRequests.projectId, projectId), eq(approvalRequests.status, status))
          : eq(approvalRequests.projectId, projectId),
      )
      .orderBy(asc(approvalRequests.requestedAt), asc(approvalRequests.id));
    return rows.map(toApproval);
  }

  async findApprovalById(id: string): Promise<ApprovalRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(approvalRequests)
      .where(eq(approvalRequests.id, id))
      .limit(1);
    return row ? toApproval(row) : undefined;
  }

  async resolveApproval(
    id: string,
    decision: "approved" | "denied",
    resolvedAt: Date,
    resolvedBy: string | null,
  ): Promise<ApprovalRecord | undefined> {
    const [row] = await this.db
      .update(approvalRequests)
      .set({
        status: decision,
        resolvedAt,
        resolvedBy,
      })
      .where(and(eq(approvalRequests.id, id), eq(approvalRequests.status, "pending")))
      .returning();
    return row ? toApproval(row) : undefined;
  }

  async consumeRateBucket(input: {
    bucketKey: string;
    windowStart: Date;
    countDelta: number;
    bytesDelta: number;
  }): Promise<RateBucketRecord> {
    const windowStartIso = input.windowStart.toISOString();
    const [row] = await this.db
      .insert(rateBuckets)
      .values({
        bucketKey: input.bucketKey,
        windowStart: input.windowStart,
        count: input.countDelta,
        bytes: BigInt(input.bytesDelta),
      })
      .onConflictDoUpdate({
        target: rateBuckets.bucketKey,
        set: {
          windowStart: sql`case when ${rateBuckets.windowStart} = ${windowStartIso}::timestamptz then ${rateBuckets.windowStart} else ${windowStartIso}::timestamptz end`,
          count: sql`case when ${rateBuckets.windowStart} = ${windowStartIso}::timestamptz then ${rateBuckets.count} + ${input.countDelta} else ${input.countDelta} end`,
          bytes: sql`case when ${rateBuckets.windowStart} = ${windowStartIso}::timestamptz then ${rateBuckets.bytes} + ${input.bytesDelta} else ${input.bytesDelta} end`,
        },
      })
      .returning();
    if (!row) {
      throw new Error("upsert rate bucket returned no row");
    }
    return toRateBucket(row);
  }
  } as TBase & Ctor<TokenStore>;
}
