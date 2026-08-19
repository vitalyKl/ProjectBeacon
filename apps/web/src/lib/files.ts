import { apiFetch, ApiError, parseJson, readApiError } from "./api";

export const FILE_EXCERPT_MAX_LINES = 400;
export const DEFAULT_TREE_DEPTH = 2;

export type RepoTreeDir = {
  path: string;
  file_count: number;
  byte_size: number;
  langs: Record<string, number>;
  important: string[] | boolean;
  children: string[];
};

export type RepoFileExcerpt = {
  path: string;
  start_line: number;
  end_line: number;
  content: string;
  lang: string;
  bytes: number;
};

export function repoTreeUrl(
  repoId: string,
  options: { path?: string; depth?: number } = {},
): string {
  const params = new URLSearchParams();
  const path = options.path?.trim();
  if (path && path !== ".") {
    params.set("path", path);
  }
  params.set("depth", String(options.depth ?? DEFAULT_TREE_DEPTH));
  return `/v1/repos/${encodeURIComponent(repoId)}/tree?${params.toString()}`;
}

export function repoFileUrl(
  repoId: string,
  options: { path: string; start_line?: number; end_line?: number },
): string {
  const params = new URLSearchParams();
  params.set("path", options.path);
  if (options.start_line !== undefined) {
    params.set("start_line", String(options.start_line));
  }
  if (options.end_line !== undefined) {
    params.set("end_line", String(options.end_line));
  }
  return `/v1/repos/${encodeURIComponent(repoId)}/files?${params.toString()}`;
}

export function isCodeIndexUnavailable(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    (error.status === 503 || error.code === "code_index_unavailable")
  );
}

export function isUnsupportedMedia(error: unknown): boolean {
  return (
    error instanceof ApiError && (error.status === 415 || error.code === "unsupported_media")
  );
}

export function posixBasename(path: string): string {
  const trimmed = path.replace(/\/+$/, "");
  if (trimmed === "" || trimmed === ".") {
    return "/";
  }
  const index = trimmed.lastIndexOf("/");
  return index === -1 ? trimmed : trimmed.slice(index + 1);
}

export function treeDirPaths(dirs: readonly RepoTreeDir[]): Set<string> {
  return new Set(dirs.map((dir) => dir.path));
}

export function treeEntryKind(path: string, dirPaths: ReadonlySet<string>): "dir" | "file" {
  return dirPaths.has(path) ? "dir" : "file";
}

export function sortTreeChildren(children: readonly string[], dirPaths: ReadonlySet<string>): string[] {
  return [...children].sort((left, right) => {
    const leftDir = dirPaths.has(left) ? 0 : 1;
    const rightDir = dirPaths.has(right) ? 0 : 1;
    if (leftDir !== rightDir) {
      return leftDir - rightDir;
    }
    return left.localeCompare(right);
  });
}

export function mergeTreeDirs(
  current: readonly RepoTreeDir[],
  incoming: readonly RepoTreeDir[],
): RepoTreeDir[] {
  const merged = new Map<string, RepoTreeDir>();
  for (const dir of current) {
    merged.set(dir.path, dir);
  }
  for (const dir of incoming) {
    merged.set(dir.path, dir);
  }
  return [...merged.values()];
}

export function excerptCanContinue(excerpt: RepoFileExcerpt): boolean {
  return excerpt.end_line - excerpt.start_line + 1 >= FILE_EXCERPT_MAX_LINES;
}

export async function fetchRepoTree(
  repoId: string,
  options: { path?: string; depth?: number } = {},
): Promise<RepoTreeDir[]> {
  const res = await apiFetch(repoTreeUrl(repoId, options));
  if (!res.ok) {
    throw await readApiError(res, "failed to load repository tree");
  }
  const body = await parseJson<{ items?: RepoTreeDir[] }>(res);
  return body.items ?? [];
}

export async function fetchRepoFile(
  repoId: string,
  options: { path: string; start_line?: number; end_line?: number },
): Promise<RepoFileExcerpt> {
  const res = await apiFetch(repoFileUrl(repoId, options));
  if (!res.ok) {
    throw await readApiError(res, "failed to load file");
  }
  return parseJson<RepoFileExcerpt>(res);
}
