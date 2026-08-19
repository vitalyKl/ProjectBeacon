import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "./api";
import {
  excerptCanContinue,
  fetchRepoFile,
  fetchRepoTree,
  FILE_EXCERPT_MAX_LINES,
  isCodeIndexUnavailable,
  isUnsupportedMedia,
  mergeTreeDirs,
  posixBasename,
  repoFileUrl,
  repoTreeUrl,
  sortTreeChildren,
  treeDirPaths,
  treeEntryKind,
  type RepoTreeDir,
} from "./files";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function dir(path: string, children: string[] = []): RepoTreeDir {
  return {
    path,
    file_count: children.length,
    byte_size: 0,
    langs: {},
    important: [],
    children,
  };
}

describe("repo URLs", () => {
  it("builds tree and file query strings for the existing /v1 routes", () => {
    expect(repoTreeUrl("repo-1")).toBe("/v1/repos/repo-1/tree?depth=2");
    expect(repoTreeUrl("repo-1", { path: ".", depth: 3 })).toBe("/v1/repos/repo-1/tree?depth=3");
    expect(repoTreeUrl("repo-1", { path: "apps/web" })).toBe(
      "/v1/repos/repo-1/tree?path=apps%2Fweb&depth=2",
    );
    expect(repoFileUrl("repo-1", { path: "apps/web/src/lib/nav.ts" })).toBe(
      "/v1/repos/repo-1/files?path=apps%2Fweb%2Fsrc%2Flib%2Fnav.ts",
    );
    expect(repoFileUrl("repo-1", { path: "README.md", start_line: 1, end_line: 400 })).toBe(
      "/v1/repos/repo-1/files?path=README.md&start_line=1&end_line=400",
    );
  });
});

describe("tree helpers", () => {
  it("treats indexed dir paths as folders and everything else as files", () => {
    const dirs = [dir(".", ["apps", "README.md"]), dir("apps", ["apps/web"])];
    const paths = treeDirPaths(dirs);
    expect(treeEntryKind(".", paths)).toBe("dir");
    expect(treeEntryKind("apps", paths)).toBe("dir");
    expect(treeEntryKind("README.md", paths)).toBe("file");
    expect(posixBasename(".")).toBe("/");
    expect(posixBasename("apps/web/src/lib/nav.ts")).toBe("nav.ts");
    expect(sortTreeChildren(["README.md", "apps", "AGENTS.md"], paths)).toEqual([
      "apps",
      "AGENTS.md",
      "README.md",
    ]);
  });

  it("merges expanded directory payloads without dropping siblings", () => {
    const root = [dir(".", ["apps"]), dir("apps", [])];
    const expanded = [dir("apps", ["apps/web"]), dir("apps/web", ["apps/web/src"])];
    expect(mergeTreeDirs(root, expanded).map((item) => item.path)).toEqual([
      ".",
      "apps",
      "apps/web",
    ]);
  });
});

describe("error and excerpt mapping", () => {
  it("recognizes index-unavailable and binary file responses", () => {
    expect(
      isCodeIndexUnavailable(new ApiError(503, "code_index_unavailable", "code index unavailable")),
    ).toBe(true);
    expect(isCodeIndexUnavailable(new ApiError(404, "not_found", "missing"))).toBe(false);
    expect(isUnsupportedMedia(new ApiError(415, "unsupported_media", "binary file"))).toBe(true);
    expect(
      excerptCanContinue({
        path: "a.ts",
        start_line: 1,
        end_line: FILE_EXCERPT_MAX_LINES,
        content: "",
        lang: "typescript",
        bytes: 10,
      }),
    ).toBe(true);
    expect(
      excerptCanContinue({
        path: "a.ts",
        start_line: 1,
        end_line: 12,
        content: "",
        lang: "typescript",
        bytes: 10,
      }),
    ).toBe(false);
  });
});

describe("fetchRepoTree and fetchRepoFile", () => {
  it("reads items from GET /tree and the excerpt from GET /files", async () => {
    const tree = [dir(".", ["README.md"])];
    const excerpt = {
      path: "README.md",
      start_line: 1,
      end_line: 2,
      content: "# Beacon",
      lang: "markdown",
      bytes: 8,
    };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: tree }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(excerpt), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(fetchRepoTree("repo-1")).resolves.toEqual(tree);
    await expect(fetchRepoFile("repo-1", { path: "README.md" })).resolves.toEqual(excerpt);
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/repos/repo-1/tree?depth=2",
      expect.objectContaining({ credentials: "same-origin" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/v1/repos/repo-1/files?path=README.md",
      expect.objectContaining({ credentials: "same-origin" }),
    );
  });

  it("maps a 503 tree response to an ApiError the UI can treat as unavailable", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ error: { code: "code_index_unavailable", message: "down" } }), {
          status: 503,
          headers: { "content-type": "application/json" },
        }),
      ),
    );
    await expect(fetchRepoTree("repo-1")).rejects.toMatchObject({
      status: 503,
      code: "code_index_unavailable",
    });
  });
});
