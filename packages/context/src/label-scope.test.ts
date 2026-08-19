import { describe, expect, it } from "vitest";

import {
  compileExtraPaths,
  labelIdsMatchingPath,
  normalizePosixPrefix,
  parentPrefix,
  pathUnderPrefix,
  scopePathsForRepo,
  uniqueScopePaths,
} from "./label-scope.js";

const REPO_A = "01934567-89ab-7cde-89ab-0123456789aa";
const REPO_B = "01934567-89ab-7cde-89ab-0123456789ab";

describe("label scope helpers", () => {
  it("normalizes POSIX prefixes and drops empties", () => {
    expect(normalizePosixPrefix("/apps/api/")).toBe("apps/api");
    expect(uniqueScopePaths([{ repo_id: REPO_A, path: "/" }])).toEqual([]);
    expect(
      uniqueScopePaths([
        { repo_id: REPO_A, path: "/apps/web/" },
        { repo_id: REPO_A, path: "apps/web" },
        { repo_id: REPO_B, path: "apps/api" },
      ]),
    ).toEqual([
      { repo_id: REPO_A, path: "apps/web" },
      { repo_id: REPO_B, path: "apps/api" },
    ]);
  });

  it("keeps only the requested repo for compile extras and changed-scope prefixes", () => {
    const paths = [
      { repo_id: REPO_A, path: "apps/web" },
      { repo_id: REPO_B, path: "apps/api" },
    ];
    expect(compileExtraPaths(paths, REPO_A)).toEqual(["apps/web"]);
    expect(compileExtraPaths(paths)).toEqual(["apps/web", "apps/api"]);
    expect(scopePathsForRepo(paths, REPO_B)).toEqual(["apps/api"]);
    expect(parentPrefix("apps/web/src/page.tsx")).toBe("apps/web/src");
    expect(parentPrefix("README.md")).toBe("");
  });

  it("matches an active label when a file sits under one of its prefixes", () => {
    expect(pathUnderPrefix("/apps/api/src/app.ts", "apps/api")).toBe(true);
    expect(pathUnderPrefix("apps/api", "apps/api")).toBe(true);
    expect(pathUnderPrefix("apps/web/page.tsx", "apps/api")).toBe(false);
    expect(
      labelIdsMatchingPath(
        [
          {
            id: "lab-api",
            status: "active",
            paths: [{ repo_id: REPO_A, path: "apps/api" }],
          },
          {
            id: "lab-web",
            status: "proposed",
            paths: [{ repo_id: REPO_A, path: "apps/web" }],
          },
          {
            id: "lab-other",
            status: "active",
            paths: [{ repo_id: REPO_B, path: "apps/api" }],
          },
        ],
        REPO_A,
        "apps/api/package.json",
      ),
    ).toEqual(["lab-api"]);
    expect(labelIdsMatchingPath([], REPO_A, "apps/api/package.json")).toEqual([]);
    expect(
      labelIdsMatchingPath(
        [{ id: "lab-api", status: "active", paths: [{ repo_id: REPO_A, path: "apps/api" }] }],
        REPO_A,
        null,
      ),
    ).toEqual([]);
  });
});
