import { userSessions, users } from "@beacon/db";
import { and, eq, isNull, sql } from "drizzle-orm";
import { BootstrapConsumedError } from "../identity.js";
import type { Db } from "@beacon/db";
import type { UserRecord, SessionRecord } from "../identity.js";
import { uniqueConstraint, mapUserInsertError, toUser, toSession, BOOTSTRAP_LOCK_KEY } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { IdentityStore } from "../identity.js";

export function withDbIdentity<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<IdentityStore> {
  return class DbIdentity extends Base {
  async hasAnyUser(): Promise<boolean> {
    const row = await this.db.select({ id: users.id }).from(users).limit(1);
    return row.length > 0;
  }

  async findUserById(id: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.id, id)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByLogin(login: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.login, login)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByEmail(email: string): Promise<UserRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(users)
      .where(sql`lower(${users.email}) = lower(${email})`)
      .limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.githubId, githubId)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    try {
      return toUser(await this.insertUser(this.db, user));
    } catch (error) {
      mapUserInsertError(error);
    }
  }

  async createFirstUser(user: UserRecord): Promise<UserRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        await tx.execute(sql`select pg_advisory_xact_lock(${BOOTSTRAP_LOCK_KEY})`);
        const existing = await tx.select({ id: users.id }).from(users).limit(1);
        if (existing.length > 0) {
          throw new BootstrapConsumedError();
        }
        return toUser(await this.insertUser(tx, user));
      });
    } catch (error) {
      if (error instanceof BootstrapConsumedError) {
        throw error;
      }
      if (uniqueConstraint(error)) {
        throw new BootstrapConsumedError();
      }
      throw error;
    }
  }

  async insertUser(
    db: Pick<Db, "insert">,
    user: UserRecord,
  ): Promise<typeof users.$inferSelect> {
    const [row] = await db
      .insert(users)
      .values({
        id: user.id,
        githubId: user.githubId,
        login: user.login,
        email: user.email,
        name: user.name,
        avatarUrl: user.avatarUrl,
        passwordHash: user.passwordHash,
        createdAt: user.createdAt,
        updatedAt: user.updatedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert user returned no row");
    }
    return row;
  }

  async createSession(session: SessionRecord): Promise<SessionRecord> {
    const [row] = await this.db
      .insert(userSessions)
      .values({
        id: session.id,
        userId: session.userId,
        tokenHash: session.tokenHash,
        createdAt: session.createdAt,
        lastSeenAt: session.lastSeenAt,
        expiresAt: session.expiresAt,
        revokedAt: session.revokedAt,
        userAgent: session.userAgent,
        ip: session.ip,
      })
      .returning();
    if (!row) {
      throw new Error("insert session returned no row");
    }
    return toSession(row);
  }

  async findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(userSessions)
      .where(eq(userSessions.tokenHash, tokenHash))
      .limit(1);
    return row ? toSession(row) : undefined;
  }

  async updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ lastSeenAt, expiresAt })
      .where(eq(userSessions.id, id));
  }

  async revokeSession(id: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.id, id), isNull(userSessions.revokedAt)));
  }

  async revokeUserSessions(userId: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.userId, userId), isNull(userSessions.revokedAt)));
  }
  } as TBase & Ctor<IdentityStore>;
}
