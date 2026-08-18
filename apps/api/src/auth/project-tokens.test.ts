import { describe, expect, it } from "vitest";

import {
  generateProjectToken,
  hashProjectToken,
  isProjectTokenFormat,
  PROJECT_TOKEN_LENGTH,
  PROJECT_TOKEN_PREFIX,
  tokenExpiresAt,
} from "./project-tokens.js";

describe("project token format", () => {
  it("is bcn_ plus 32-byte base64url (47 chars)", () => {
    const token = generateProjectToken();
    expect(token.startsWith(PROJECT_TOKEN_PREFIX)).toBe(true);
    expect(token).toHaveLength(PROJECT_TOKEN_LENGTH);
    expect(isProjectTokenFormat(token)).toBe(true);
    expect(hashProjectToken(token)).toHaveLength(32);
  });

  it("defaults TTL to 90 days and allows no expiry", () => {
    const now = new Date("2026-01-01T00:00:00.000Z");
    expect(tokenExpiresAt(now, "90d")).toEqual(new Date("2026-04-01T00:00:00.000Z"));
    expect(tokenExpiresAt(now, "none")).toBeNull();
  });
});
