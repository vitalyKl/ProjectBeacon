import { describe, expect, it } from "vitest";

import { resolveLocalRepoId } from "./local-code.js";

const ctx = {
  baseUrl: "http://127.0.0.1:8080",
  token: "bcn_test",
  projectId: "01934567-89ab-7cde-89ab-0123456789ac",
};

describe("resolveLocalRepoId", () => {
  it("uses the sole listed repo when default_repo_id is omitted", () => {
    expect(resolveLocalRepoId(undefined, ctx, { roots: {} }, [{ id: "repo-only" }])).toBe(
      "repo-only",
    );
  });

  it("throws repo_ambiguous when more than one repo exists and no default", () => {
    expect(() =>
      resolveLocalRepoId(undefined, ctx, { roots: {} }, [{ id: "a" }, { id: "b" }]),
    ).toThrowError(/repo_id is required/);
  });
});
