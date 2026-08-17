export type UserRecord = {
  id: string;
  githubId: bigint | null;
  login: string;
  email: string | null;
  name: string | null;
  avatarUrl: string | null;
  passwordHash: string | null;
  createdAt: Date;
  updatedAt: Date;
};

export type SessionRecord = {
  id: string;
  userId: string;
  tokenHash: Buffer;
  createdAt: Date;
  lastSeenAt: Date;
  expiresAt: Date;
  revokedAt: Date | null;
  userAgent: string | null;
  ip: string | null;
};

export class LoginTakenError extends Error {
  override readonly name = "LoginTakenError";

  constructor() {
    super("login is already taken");
  }
}

export interface AuthStore {
  hasAnyUser(): Promise<boolean>;
  findUserById(id: string): Promise<UserRecord | undefined>;
  findUserByLogin(login: string): Promise<UserRecord | undefined>;
  findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined>;
  createUser(user: UserRecord): Promise<UserRecord>;
  createSession(session: SessionRecord): Promise<SessionRecord>;
  findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined>;
  updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void>;
  revokeSession(id: string, revokedAt: Date): Promise<void>;
  revokeUserSessions(userId: string, revokedAt: Date): Promise<void>;
}

function cloneUser(user: UserRecord): UserRecord {
  return { ...user };
}

function cloneSession(session: SessionRecord): SessionRecord {
  return {
    ...session,
    tokenHash: Buffer.from(session.tokenHash),
    createdAt: new Date(session.createdAt),
    lastSeenAt: new Date(session.lastSeenAt),
    expiresAt: new Date(session.expiresAt),
    revokedAt: session.revokedAt ? new Date(session.revokedAt) : null,
  };
}

export class MemoryAuthStore implements AuthStore {
  private readonly users = new Map<string, UserRecord>();
  private readonly sessions = new Map<string, SessionRecord>();

  async hasAnyUser(): Promise<boolean> {
    return this.users.size > 0;
  }

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

  async createUser(user: UserRecord): Promise<UserRecord> {
    if (await this.findUserByLogin(user.login)) {
      throw new LoginTakenError();
    }
    if (user.githubId !== null && (await this.findUserByGithubId(user.githubId))) {
      throw new LoginTakenError();
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
}
