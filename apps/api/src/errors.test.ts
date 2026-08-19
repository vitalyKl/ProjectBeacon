import { describe, expect, it } from "vitest";

import { isMissingSchemaError } from "./errors.js";

describe("isMissingSchemaError", () => {
  it("detects postgres undefined_table and wrapped drizzle causes", () => {
    expect(isMissingSchemaError(Object.assign(new Error("nope"), { code: "42P01" }))).toBe(true);
    expect(isMissingSchemaError(Object.assign(new Error("nope"), { code: "42703" }))).toBe(true);
    expect(
      isMissingSchemaError({
        message: "Failed query",
        cause: Object.assign(new Error('relation "project_reports" does not exist'), { code: "42P01" }),
      }),
    ).toBe(true);
    expect(isMissingSchemaError(new Error("unique violation"))).toBe(false);
  });
});
