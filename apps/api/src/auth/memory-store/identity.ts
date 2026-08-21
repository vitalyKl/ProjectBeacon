import { BootstrapConsumedError, GithubIdTakenError, LoginTakenError } from "../identity.js";
import type { UserRecord, SessionRecord } from "../identity.js";
import { cloneUser, cloneSession, emailsEqual } from "./clone.js";
import type { Ctor } from "./ctor.js";
import { MemoryStoreCore } from "./core.js";

export function withMemoryIdentity<TBase extends Ctor<MemoryStoreCore>>(Base: TBase) {
  return class MemoryIdentity extends Base {
  async findUserById(id: string): Promise<UserRecord | undefined> {
    const user = this.users.get(id);
    return user ? cloneUser(user) : undefined;
  }

  async findUserByLogin(login: string): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (user.login === login) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (user.githubId === githubId) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async findUserByEmail(email: string): Promise<UserRecord | undefined> {
    for (const user of this.users.values()) {
      if (emailsEqual(user.email, email)) {
        return cloneUser(user);
      }
    }
    return undefined;
  }

  async hasAnyUser(): Promise<boolean> {
    return this.users.size > 0;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    return this.enqueueWrite(() => this.insertUser(user));
  }

  async createFirstUser(user: UserRecord): Promise<UserRecord> {
    return this.enqueueWrite(() => {
      if (this.users.size > 0) {
        throw new BootstrapConsumedError();
      }
      return this.insertUser(user);
    });
  }

  insertUser(user: UserRecord): UserRecord {
    for (const existing of this.users.values()) {
      if (existing.login === user.login) {
        throw new LoginTakenError();
      }
      if (user.githubId !== null && existing.githubId === user.githubId) {
        throw new GithubIdTakenError();
      }
    }
    this.users.set(user.id, cloneUser(user));
    return cloneUser(user);
  }

  async createSession(session: SessionRecord): Promise<SessionRecord> {
    this.sessions.set(session.id, cloneSession(session));
    return cloneSession(session);
  }

  async findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined> {
    for (const session of this.sessions.values()) {
      if (session.tokenHash.equals(tokenHash)) {
        return cloneSession(session);
      }
    }
    return undefined;
  }

  async updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void> {
    const session = this.sessions.get(id);
    if (!session) {
      return;
    }
    session.lastSeenAt = new Date(lastSeenAt);
    session.expiresAt = new Date(expiresAt);
  }

  async revokeSession(id: string, revokedAt: Date): Promise<void> {
    const session = this.sessions.get(id);
    if (!session || session.revokedAt) {
      return;
    }
    session.revokedAt = new Date(revokedAt);
  }

  async revokeUserSessions(userId: string, revokedAt: Date): Promise<void> {
    for (const session of this.sessions.values()) {
      if (session.userId === userId && !session.revokedAt) {
        session.revokedAt = new Date(revokedAt);
      }
    }
  }
  };
}
