import { uuidv7 } from "@beacon/shared";

import {
  generateSessionToken,
  hashSessionToken,
  ROLLING_REFRESH_AFTER_MS,
  SESSION_TTL_MS,
} from "./tokens.js";
import type { AuthStore, SessionRecord, UserRecord } from "./store.js";

export type PublicUser = {
  id: string;
  login: string;
  email: string | null;
  name: string | null;
  avatar_url: string | null;
};

export function toPublicUser(user: UserRecord): PublicUser {
  return {
    id: user.id,
    login: user.login,
    email: user.email,
    name: user.name,
    avatar_url: user.avatarUrl,
  };
}

export async function issueSession(
  store: AuthStore,
  user: UserRecord,
  now: Date,
  meta: { userAgent: string | null; ip: string | null },
): Promise<{ token: string; session: SessionRecord }> {
  const token = generateSessionToken();
  const session: SessionRecord = {
    id: uuidv7(now.getTime()),
    userId: user.id,
    tokenHash: hashSessionToken(token),
    createdAt: now,
    lastSeenAt: now,
    expiresAt: new Date(now.getTime() + SESSION_TTL_MS),
    revokedAt: null,
    userAgent: meta.userAgent,
    ip: meta.ip,
  };
  await store.createSession(session);
  return { token, session };
}

export async function lookupValidSession(
  store: AuthStore,
  token: string | undefined,
  now: Date,
): Promise<{ user: UserRecord; session: SessionRecord } | undefined> {
  if (!token) {
    return undefined;
  }

  const session = await store.findSessionByTokenHash(hashSessionToken(token));
  if (!session || session.revokedAt || session.expiresAt.getTime() <= now.getTime()) {
    return undefined;
  }

  const user = await store.findUserById(session.userId);
  if (!user) {
    return undefined;
  }

  return { user, session };
}

export async function resolveSession(
  store: AuthStore,
  token: string | undefined,
  now: Date,
): Promise<{ user: UserRecord; session: SessionRecord; rolled: boolean } | undefined> {
  const found = await lookupValidSession(store, token, now);
  if (!found) {
    return undefined;
  }

  const { user, session } = found;
  let rolled = false;
  if (now.getTime() - session.lastSeenAt.getTime() > ROLLING_REFRESH_AFTER_MS) {
    const expiresAt = new Date(now.getTime() + SESSION_TTL_MS);
    await store.updateSessionRolling(session.id, now, expiresAt);
    session.lastSeenAt = now;
    session.expiresAt = expiresAt;
    rolled = true;
  }

  return { user, session, rolled };
}
