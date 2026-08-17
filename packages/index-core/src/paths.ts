import path from "node:path";

const DRIVE_PREFIX = /^[A-Za-z]:/;

export class PathEscapeError extends Error {
  override readonly name = "PathEscapeError";
  constructor(input: string) {
    super(`path escapes repo root: ${input}`);
  }
}

export function toPosix(input: string): string {
  return input.replaceAll("\\", "/");
}

function collapsePosix(input: string): string {
  const posix = toPosix(input);
  const drive = posix.match(DRIVE_PREFIX)?.[0] ?? "";
  const rest = drive ? posix.slice(drive.length) : posix;
  const isAbs = rest.startsWith("/");
  const parts: string[] = [];
  for (const part of rest.split("/")) {
    if (part === "" || part === ".") {
      continue;
    }
    if (part === "..") {
      if (parts.length === 0 || parts[parts.length - 1] === "..") {
        if (!isAbs) {
          parts.push("..");
        }
        continue;
      }
      parts.pop();
      continue;
    }
    parts.push(part);
  }
  const joined = parts.join("/");
  if (drive) {
    return `${drive}/${joined}`.replace(/\/+$/, "") || `${drive}/`;
  }
  if (isAbs) {
    return `/${joined}`;
  }
  return joined || ".";
}

export function normalizeRepoRoot(repoRoot: string): string {
  return path.resolve(repoRoot);
}

/**
 * Convert a user/OS path into a repo-relative POSIX path stored in SQLite.
 * Windows `\` becomes `/`. In-repo drive prefixes are stripped. Escapes throw.
 */
export function toRepoPosixPath(repoRoot: string, input: string): string {
  const logicalRoot = collapsePosix(repoRoot);
  const resolvedRoot = collapsePosix(path.resolve(repoRoot));
  const inputPosix = toPosix(input);
  const inputHasDrive = DRIVE_PREFIX.test(inputPosix);
  const inputIsPosixAbs = inputPosix.startsWith("/");
  const inputIsWinAbs = path.win32.isAbsolute(input);

  const candidateRoots = logicalRoot === resolvedRoot ? [resolvedRoot] : [logicalRoot, resolvedRoot];
  const sameFamilyAbsolute =
    inputHasDrive ||
    inputIsWinAbs ||
    (inputIsPosixAbs && candidateRoots.some((root) => root.startsWith("/")));

  const absolute = sameFamilyAbsolute
    ? collapsePosix(inputPosix)
    : collapsePosix(`${resolvedRoot}/${inputPosix}`);

  for (const rootPosix of candidateRoots) {
    if (absolute === rootPosix) {
      return ".";
    }
    const prefix = rootPosix.endsWith("/") ? rootPosix : `${rootPosix}/`;
    if (absolute.startsWith(prefix)) {
      return absolute.slice(prefix.length);
    }
  }
  if (!sameFamilyAbsolute) {
    const prefix = resolvedRoot.endsWith("/") ? resolvedRoot : `${resolvedRoot}/`;
    if (absolute.startsWith(prefix)) {
      return absolute.slice(prefix.length);
    }
  }
  throw new PathEscapeError(input);
}

export function toFsPath(repoRoot: string, repoPosixPath: string): string {
  if (repoPosixPath === "." || repoPosixPath === "") {
    return normalizeRepoRoot(repoRoot);
  }
  const relative = repoPosixPath.split("/").join(path.sep);
  const resolved = path.resolve(repoRoot, relative);
  toRepoPosixPath(repoRoot, resolved);
  return resolved;
}

export function parentDir(repoPosixPath: string): string | null {
  if (repoPosixPath === "." || repoPosixPath === "") {
    return null;
  }
  const idx = repoPosixPath.lastIndexOf("/");
  if (idx < 0) {
    return ".";
  }
  return repoPosixPath.slice(0, idx) || ".";
}

export function basename(repoPosixPath: string): string {
  const idx = repoPosixPath.lastIndexOf("/");
  return idx < 0 ? repoPosixPath : repoPosixPath.slice(idx + 1);
}

export function joinPosix(...parts: string[]): string {
  const filtered = parts.filter((part) => part !== "" && part !== ".");
  if (filtered.length === 0) {
    return ".";
  }
  return filtered
    .join("/")
    .replace(/\/+/g, "/")
    .replace(/\/\.\//g, "/")
    .replace(/^\.\//, "");
}
