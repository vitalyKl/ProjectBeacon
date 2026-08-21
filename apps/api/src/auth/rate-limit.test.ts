import { describe, expect, it } from "vitest";

import { MemoryAuthStore } from "./store.js";
import { consumeRateLimit, DEFAULT_RATE_LIMITS } from "./rate-limit.js";

describe("consumeRateLimit login", () => {
  it("rejects the N+1 auth attempt in the minute window", async () => {
    const store = new MemoryAuthStore();
    const now = new Date("2026-08-21T00:00:00.000Z");
    const limits = { ...DEFAULT_RATE_LIMITS, loginPerMin: 2, burstMultiplier: 1 };

    const first = await consumeRateLimit(store, "token", "auth-login:admin", limits, now, "login");
    const second = await consumeRateLimit(store, "token", "auth-login:admin", limits, now, "login");
    const third = await consumeRateLimit(store, "token", "auth-login:admin", limits, now, "login");

    expect(first).toEqual({ ok: true });
    expect(second).toEqual({ ok: true });
    expect(third.ok).toBe(false);
    if (!third.ok) {
      expect(third.retryAfter).toBeGreaterThan(0);
    }
  });
});
