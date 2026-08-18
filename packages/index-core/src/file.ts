import fs from "node:fs";

import { BINARY_PROBE_BYTES, containsNul } from "./denylist.js";
import { PathEscapeError, toFsPath, toRepoPosixPath } from "./paths.js";
import type { LanguageId } from "./types.js";
import { detectLanguage } from "./languages.js";
import type { FileRecord } from "./types.js";

export const GET_FILE_MAX_LINES = 400;

export type FileExcerpt =
  | {
      ok: true;
      path: string;
      startLine: number;
      endLine: number;
      content: string;
      lang: LanguageId;
      bytes: number;
    }
  | { ok: false; reason: "missing" | "binary" | "escape" };

function probeBinary(absPath: string): boolean {
  const fd = fs.openSync(absPath, "r");
  try {
    const probe = Buffer.alloc(BINARY_PROBE_BYTES);
    const read = fs.readSync(fd, probe, 0, probe.length, 0);
    return containsNul(probe.subarray(0, read));
  } finally {
    fs.closeSync(fd);
  }
}

export function readFileExcerpt(
  repoRoot: string,
  inputPath: string,
  options: { startLine?: number; endLine?: number; indexed?: FileRecord | null } = {},
): FileExcerpt {
  let repoPath: string;
  try {
    repoPath = toRepoPosixPath(repoRoot, inputPath);
  } catch (error) {
    if (error instanceof PathEscapeError) {
      return { ok: false, reason: "escape" };
    }
    throw error;
  }

  if (options.indexed?.isBinary) {
    return { ok: false, reason: "binary" };
  }

  let absPath: string;
  try {
    absPath = toFsPath(repoRoot, repoPath);
  } catch (error) {
    if (error instanceof PathEscapeError) {
      return { ok: false, reason: "escape" };
    }
    throw error;
  }

  let stat: fs.Stats;
  try {
    stat = fs.statSync(absPath);
  } catch {
    return { ok: false, reason: "missing" };
  }
  if (!stat.isFile()) {
    return { ok: false, reason: "missing" };
  }
  if (probeBinary(absPath)) {
    return { ok: false, reason: "binary" };
  }

  const source = fs.readFileSync(absPath, "utf8");
  const lines = source.split(/\r?\n/);
  const startLine = Math.max(1, options.startLine ?? 1);
  const requestedEnd = options.endLine ?? startLine + GET_FILE_MAX_LINES - 1;
  const endLine = Math.min(lines.length, requestedEnd, startLine + GET_FILE_MAX_LINES - 1);
  if (startLine > lines.length) {
    return {
      ok: true,
      path: repoPath,
      startLine,
      endLine: startLine - 1,
      content: "",
      lang: options.indexed?.lang ?? detectLanguage(repoPath),
      bytes: 0,
    };
  }
  const slice = lines.slice(startLine - 1, endLine);
  const content = slice.join("\n");
  return {
    ok: true,
    path: repoPath,
    startLine,
    endLine,
    content,
    lang: options.indexed?.lang ?? detectLanguage(repoPath),
    bytes: Buffer.byteLength(content),
  };
}
