import { describe, expect, it } from "vitest";

import { ERROR_CODES, isErrorCode } from "./error-codes.js";

describe("ERROR_CODES", () => {
  it("distinguishes invalid_request from unauthorized", () => {
    expect(ERROR_CODES).toContain("unauthorized");
    expect(ERROR_CODES).toContain("invalid_request");
    expect(isErrorCode("invalid_request")).toBe(true);
    expect(isErrorCode("unauthorized")).toBe(true);
    expect(isErrorCode("bad_request")).toBe(false);
  });
});
