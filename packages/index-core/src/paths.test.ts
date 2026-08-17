import os from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { PathEscapeError, toPosix, toRepoPosixPath } from "./paths.js";

describe("path normalization", () => {
  it("stores POSIX paths and converts Windows separators", () => {
    const root = path.join("C:", "repo");
    const posix = toPosix("src\\lib\\index.ts");
    expect(posix).toBe("src/lib/index.ts");
    expect(toRepoPosixPath(root, "src\\lib\\index.ts")).toBe("src/lib/index.ts");
  });

  it("strips a drive-letter prefix for in-repo Windows paths", () => {
    const root = "D:\\work\\beacon";
    expect(toRepoPosixPath(root, "D:\\work\\beacon\\src\\main.ts")).toBe("src/main.ts");
    expect(toRepoPosixPath(root, "D:/work/beacon/readme.md")).toBe("readme.md");
  });

  it("rejects paths that escape the repo root", () => {
    const root = path.join(os.tmpdir(), "beacon-index-root");
    expect(() => toRepoPosixPath(root, "../outside.ts")).toThrow(PathEscapeError);
    expect(() => toRepoPosixPath(root, "..\\outside.ts")).toThrow(PathEscapeError);
    expect(() => toRepoPosixPath(root, path.join(root, "..", "other", "file.ts"))).toThrow(
      PathEscapeError,
    );
    expect(() => toRepoPosixPath("D:\\work\\beacon", "C:\\Windows\\system32\\cmd.exe")).toThrow(
      PathEscapeError,
    );
  });

  it("normalizes mac/linux style absolute in-repo paths", () => {
    expect(toRepoPosixPath("/Users/dev/repo", "/Users/dev/repo/src/app.ts")).toBe("src/app.ts");
    expect(toRepoPosixPath("/Users/dev/repo", "./src/app.ts")).toBe("src/app.ts");
  });
});
