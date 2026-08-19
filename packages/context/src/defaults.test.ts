import { describe, expect, it } from "vitest";

import {
  DEFAULT_PROJECT_LABELS,
  DEFAULT_PROJECT_LABEL_STATUS,
  suggestedLabelPrefixes,
} from "./defaults.js";

describe("default project labels", () => {
  it("seeds a unique active catalog without path prefixes", () => {
    const slugs = DEFAULT_PROJECT_LABELS.map((label) => label.slug);
    expect(new Set(slugs).size).toBe(slugs.length);
    expect(slugs).toEqual(["api", "web", "cli", "visual", "ux"]);
    expect(DEFAULT_PROJECT_LABEL_STATUS).toBe("active");
    expect(DEFAULT_PROJECT_LABELS.every((label) => /^#[0-9a-f]{6}$/.test(label.color))).toBe(true);
  });

  it("suggests one POSIX prefix per matching starter slug", () => {
    expect(
      suggestedLabelPrefixes([
        "apps/api/package.json",
        "apps/web/package.json",
        "apps/cli/src/index.ts",
        "packages/ui/src/button.tsx",
        "AGENTS.md",
      ]),
    ).toEqual([
      { slug: "api", path: "apps/api" },
      { slug: "web", path: "apps/web" },
      { slug: "cli", path: "apps/cli" },
      { slug: "visual", path: "packages/ui" },
      { slug: "ux", path: "apps/web" },
    ]);
  });

  it("returns nothing when the layout has no known area directories", () => {
    expect(suggestedLabelPrefixes(["package.json", "README.md", "src/index.ts"])).toEqual([]);
    expect(suggestedLabelPrefixes(["api/go.mod"])).toEqual([{ slug: "api", path: "api" }]);
  });
});
