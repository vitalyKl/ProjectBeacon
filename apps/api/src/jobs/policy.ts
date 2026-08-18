export const BRIEF_RETENTION_PER_PROJECT = 200;
export const ACTIVITY_RETENTION_MS = 90 * 24 * 60 * 60 * 1000;
export const ACTIVITY_DELETE_BATCH = 5_000;
export const IDEMPOTENCY_RETENTION_MS = 24 * 60 * 60 * 1000;
export const USER_SESSION_RETENTION_MS = 7 * 24 * 60 * 60 * 1000;

export type RetentionCounts = {
  activityDeleted: number;
  briefsDeleted: number;
  idempotencyDeleted: number;
  sessionsDeleted: number;
};

export type ExpireLocksCounts = {
  locksReleased: number;
  sessionsAbandoned: number;
};

export function emptyRetentionCounts(): RetentionCounts {
  return {
    activityDeleted: 0,
    briefsDeleted: 0,
    idempotencyDeleted: 0,
    sessionsDeleted: 0,
  };
}

export function idsOlderThanKeep(
  items: readonly { id: string; createdAt: Date }[],
  keep: number,
): string[] {
  return [...items]
    .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime() || b.id.localeCompare(a.id))
    .slice(keep)
    .map((item) => item.id);
}

export function isUserSessionPastRetention(
  session: { revokedAt: Date | null; expiresAt: Date },
  now: Date,
): boolean {
  const cutoff = now.getTime() - USER_SESSION_RETENTION_MS;
  return (
    (session.revokedAt !== null && session.revokedAt.getTime() <= cutoff) ||
    session.expiresAt.getTime() <= cutoff
  );
}
