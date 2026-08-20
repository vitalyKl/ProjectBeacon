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

export class BootstrapConsumedError extends Error {
  override readonly name = "BootstrapConsumedError";

  constructor() {
    super("bootstrap has already been consumed");
  }
}

export class GithubIdTakenError extends Error {
  override readonly name = "GithubIdTakenError";

  constructor() {
    super("github account is already linked");
  }
}

export interface IdentityStore {
  hasAnyUser(): Promise<boolean>;
  findUserById(id: string): Promise<UserRecord | undefined>;
  findUserByLogin(login: string): Promise<UserRecord | undefined>;
  findUserByEmail(email: string): Promise<UserRecord | undefined>;
  findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined>;
  createUser(user: UserRecord): Promise<UserRecord>;
  createFirstUser(user: UserRecord): Promise<UserRecord>;
  createSession(session: SessionRecord): Promise<SessionRecord>;
  findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined>;
  updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void>;
  revokeSession(id: string, revokedAt: Date): Promise<void>;
  revokeUserSessions(userId: string, revokedAt: Date): Promise<void>;
}
