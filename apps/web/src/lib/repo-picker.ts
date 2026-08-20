import type { PublicRepo } from "./api";
import { repoDisplayName } from "./index-status";

export type RepoPickerOption = {
  id: string;
  label: string;
};

export type RepoPickerCatalogState = "loading" | "empty" | "error" | "ready";

export function projectRepoCatalog<T extends { id: string }>(input: {
  projectId: string | null | undefined;
  loadedProjectId: string | null;
  repos: readonly T[] | null;
  failed: boolean;
  selectedId?: string;
}): {
  repos: readonly T[] | null;
  failed: boolean;
  selectedId: string;
  state: RepoPickerCatalogState;
} {
  const matches = Boolean(input.projectId) && input.loadedProjectId === input.projectId;
  const repos = matches ? input.repos : null;
  const failed = matches && input.failed;
  const requestedId = (input.selectedId ?? "").trim();
  const selectedId = repos?.some((repo) => repo.id === requestedId) ? requestedId : "";
  return {
    repos,
    failed,
    selectedId,
    state: repoPickerCatalogState({ repos, failed }),
  };
}

export function repoPickerCatalogState(input: {
  repos: readonly unknown[] | null | undefined;
  failed: boolean;
}): RepoPickerCatalogState {
  if (input.failed) {
    return "error";
  }
  if (input.repos == null) {
    return "loading";
  }
  if (input.repos.length === 0) {
    return "empty";
  }
  return "ready";
}

export function repoPickerOptions(
  repos: readonly PublicRepo[] | null | undefined,
  selectedId = "",
): RepoPickerOption[] {
  const options: RepoPickerOption[] = (repos ?? []).map((repo) => ({
    id: repo.id,
    label: repoDisplayName(repo),
  }));
  const trimmed = selectedId.trim();
  if (trimmed && !options.some((option) => option.id === trimmed)) {
    options.push({ id: trimmed, label: trimmed });
  }
  return options;
}
