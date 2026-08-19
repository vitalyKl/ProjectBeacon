import { describe, expect, it } from "vitest";

import { activeLabelPaths, extraCompilePaths } from "./scope.js";
import type { LabelRecord } from "./types.js";

const REPO = "01934567-89ab-7cde-89ab-0123456789aa";

function label(overrides: Partial<LabelRecord>): LabelRecord {
  return {
    id: "01934567-89ab-7cde-89ab-0123456789a1",
    projectId: "01934567-89ab-7cde-89ab-0123456789a0",
    slug: "api",
    name: "API",
    description: "",
    color: null,
    status: "active",
    createdAt: new Date("2026-08-18T00:00:00.000Z"),
    paths: [{ repo_id: REPO, path: "apps/api" }],
    ...overrides,
  };
}

describe("label compile scope", () => {
  it("uses only active labels when expanding extra paths", () => {
    const proposed = label({
      id: "01934567-89ab-7cde-89ab-0123456789a2",
      slug: "visual",
      name: "Visual",
      status: "proposed",
      paths: [{ repo_id: REPO, path: "apps/web" }],
    });
    expect(extraCompilePaths([label({}), proposed], REPO)).toEqual(["apps/api"]);
    expect(activeLabelPaths([label({}), proposed])).toEqual([{ repo_id: REPO, path: "apps/api" }]);
  });
});
