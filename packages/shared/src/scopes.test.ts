import { describe, expect, it } from "vitest";

import { DEFAULT_TOKEN_SCOPES, isScope, SCOPES } from "./scopes.js";

describe("DEFAULT_TOKEN_SCOPES", () => {
  it("omits code:read and write-only admin scopes", () => {
    expect(DEFAULT_TOKEN_SCOPES).not.toContain("code:read");
    expect(DEFAULT_TOKEN_SCOPES).not.toContain("admin");
    expect(DEFAULT_TOKEN_SCOPES).not.toContain("constraints:apply");
    expect(DEFAULT_TOKEN_SCOPES).not.toContain("code:write");
    expect(DEFAULT_TOKEN_SCOPES).not.toContain("constraints:propose");
    for (const scope of DEFAULT_TOKEN_SCOPES) {
      expect(isScope(scope)).toBe(true);
    }
    expect(SCOPES).not.toContain("code:write");
    expect(SCOPES).not.toContain("constraints:propose");
  });
});
