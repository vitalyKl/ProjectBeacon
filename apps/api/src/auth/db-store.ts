import { userSessions, users, type Db } from "@beacon/db";
import { and, eq, isNull } from "drizzle-orm";

import {
  LoginTakenError,
  type AuthStore,
  type SessionRecord,
  type UserRecord,
} from "./store.js";

function isUniqueViolation(error: unknown): boolean {
  let current: unknown = error;
  for (let i = 0; i < 4 && current; i += 1) {
    if (
      typeof current === "object" &&
      current !== null &&
      "code" in current &&
      (current as { code: unknown }).code === "23505"
    ) {
      return true;
    }
    current =
      typeof current === "object" && current !== null && "cause" in current
        ? (current as { cause: unknown }).cause
        : undefined;
  }
  return false;
}

function asBuffer(value: Buffer | Uint8Array): Buffer {
  return Buffer.isBuffer(value) ? value : Buffer.from(value);
}

function toUser(row: typeof users.$inferSelect): UserRecord {
  return {
    id: row.id,
    githubId: row.githubId,
    login: row.login,
    email: row.email,
    name: row.name,
    avatarUrl: row.avatarUrl,
    passwordHash: row.passwordHash,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toSession(row: typeof userSessions.$inferSelect): SessionRecord {
  return {
    id: row.id,
    userId: row.userId,
    tokenHash: asBuffer(row.tokenHash),
    createdAt: row.createdAt,
    lastSeenAt: row.lastSeenAt,
    expiresAt: row.expiresAt,
    revokedAt: row.revokedAt,
    userAgent: row.userAgent,
    ip: row.ip,
  };
}

export class DbAuthStore implements AuthStore {
  constructor(private readonly db: Db) {}

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

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.githubId, githubId)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    try {
      const [row] = await this.db
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
      return toUser(row);
    } catch (error) {
      if (isUniqueViolation(error)) {
        throw new LoginTakenError();
      }
      throw error;
    }
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
}
