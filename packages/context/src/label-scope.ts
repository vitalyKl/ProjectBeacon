export type ScopePath = {
  repo_id: string;
  path: string;
};

export function normalizePosixPrefix(path: string): string {
  return path.replace(/^\/+/, "").replace(/\/+$/, "");
}

export function uniqueScopePaths(paths: ScopePath[]): ScopePath[] {
  const seen = new Set<string>();
  const result: ScopePath[] = [];
  for (const item of paths) {
    const path = normalizePosixPrefix(item.path);
    if (!path) {
      continue;
    }
    const key = `${item.repo_id}:${path}`;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    result.push({ repo_id: item.repo_id, path });
  }
  return result;
}

export function scopePathsForRepo(paths: ScopePath[], repoId: string): string[] {
  return uniqueScopePaths(paths)
    .filter((item) => item.repo_id === repoId)
    .map((item) => item.path);
}

export function compileExtraPaths(paths: ScopePath[], repoId?: string | null): string[] {
  const scoped = repoId ? paths.filter((item) => item.repo_id === repoId) : paths;
  return uniqueScopePaths(scoped).map((item) => item.path);
}

export function parentPrefix(path: string): string {
  const normalized = normalizePosixPrefix(path);
  const index = normalized.lastIndexOf("/");
  return index === -1 ? "" : normalized.slice(0, index);
}

export function pathUnderPrefix(path: string, prefix: string): boolean {
  const normalizedPath = normalizePosixPrefix(path);
  const normalizedPrefix = normalizePosixPrefix(prefix);
  if (!normalizedPath || !normalizedPrefix) {
    return false;
  }
  return (
    normalizedPath === normalizedPrefix || normalizedPath.startsWith(`${normalizedPrefix}/`)
  );
}

export type MatchingLabel = {
  id: string;
  status?: string;
  paths: ScopePath[];
};

export function labelIdsMatchingPath(
  labels: readonly MatchingLabel[],
  repoId: string,
  path: string | null | undefined,
): string[] {
  if (!path) {
    return [];
  }
  const ids: string[] = [];
  for (const label of labels) {
    if (label.status && label.status !== "active") {
      continue;
    }
    if (scopePathsForRepo(label.paths, repoId).some((prefix) => pathUnderPrefix(path, prefix))) {
      ids.push(label.id);
    }
  }
  return ids;
}
