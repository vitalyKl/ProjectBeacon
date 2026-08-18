import { describe, expect, it } from "vitest";

import {
  BRIEF_RETENTION_PER_PROJECT,
  USER_SESSION_RETENTION_MS,
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
});
