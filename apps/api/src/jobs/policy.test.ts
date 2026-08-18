import { describe, expect, it } from "vitest";

import {
  BRIEF_RETENTION_PER_PROJECT,
  USER_SESSION_RETENTION_MS,
  decideExpiredLocks,
  idsOlderThanKeep,
  isUserSessionPastRetention,
} from "./policy.js";

describe("retention policy", () => {
  it("keeps the newest ids and drops the rest in createdAt then id order", () => {
    const now = new Date("2026-01-01T00:00:00.000Z");
    const items = [
      { id: "a", createdAt: now },
      { id: "c", createdAt: now },
      { id: "b", createdAt: new Date("2026-01-02T00:00:00.000Z") },
    ];
    expect(idsOlderThanKeep(items, 2)).toEqual(["a"]);
    expect(idsOlderThanKeep(items, BRIEF_RETENTION_PER_PROJECT)).toEqual([]);
  });

  it("drops revoked or expired user sessions after seven days", () => {
    const now = new Date("2026-02-01T00:00:00.000Z");
    const cutoff = new Date(now.getTime() - USER_SESSION_RETENTION_MS);
    expect(
      isUserSessionPastRetention(
        { revokedAt: cutoff, expiresAt: new Date("2026-03-01T00:00:00.000Z") },
        now,
      ),
    ).toBe(true);
    expect(
      isUserSessionPastRetention(
        { revokedAt: null, expiresAt: new Date(cutoff.getTime() + 1) },
        now,
      ),
    ).toBe(false);
    expect(isUserSessionPastRetention({ revokedAt: null, expiresAt: cutoff }, now)).toBe(true);
  });

  it("does not abandon or release a lock after the holder renews", () => {
    const now = new Date("2026-01-01T05:00:00.000Z");
    const expired = new Date("2026-01-01T00:00:00.000Z");
    const renewed = new Date("2026-01-01T09:00:00.000Z");
    const decided = decideExpiredLocks(
      [
        {
          id: "session-1",
          status: "active",
          lockExpiresAt: renewed,
          taskId: "task-1",
        },
      ],
      [
        {
          id: "task-1",
          lockedBySessionId: "session-1",
          lockExpiresAt: expired,
        },
      ],
      now,
    );
    expect(decided).toEqual({ sessionIds: [], taskIds: [] });
  });

  it("abandons a still-expired holder and a session whose own lease lapsed", () => {
    const now = new Date("2026-01-01T05:00:00.000Z");
    const expired = new Date("2026-01-01T00:00:00.000Z");
    const decided = decideExpiredLocks(
      [
        {
          id: "holder",
          status: "active",
          lockExpiresAt: expired,
          taskId: "task-1",
        },
        {
          id: "orphan",
          status: "active",
          lockExpiresAt: expired,
          taskId: null,
        },
      ],
      [
        {
          id: "task-1",
          lockedBySessionId: "holder",
          lockExpiresAt: expired,
        },
      ],
      now,
    );
    expect(decided.sessionIds.sort()).toEqual(["holder", "orphan"]);
    expect(decided.taskIds).toEqual(["task-1"]);
  });
});
