import { describe, expect, it } from "vitest";

import { isHostedCloneEnabled } from "./flags.js";

describe("isHostedCloneEnabled", () => {
  it("reads ff.hosted_clone and FF_HOSTED_CLONE at call time", () => {
    expect(isHostedCloneEnabled({})).toBe(false);
    expect(isHostedCloneEnabled({ "ff.hosted_clone": "true" })).toBe(true);
    expect(isHostedCloneEnabled({ FF_HOSTED_CLONE: "true" })).toBe(true);
    expect(isHostedCloneEnabled({ "ff.hosted_clone": "false", FF_HOSTED_CLONE: "true" })).toBe(
      false,
    );
  });
});
