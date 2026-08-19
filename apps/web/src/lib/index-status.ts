import { fetchProjectRepos, fetchRepo, type PublicRepo } from "./api";
import { t, type MessageKey } from "./i18n";

export async function fetchDetailedProjectRepos(projectId: string): Promise<PublicRepo[] | null> {
  const listed = await fetchProjectRepos(projectId);
  if (!listed) {
    return listed;
  }
  return Promise.all(listed.map(async (repo) => (await fetchRepo(repo.id)) ?? repo));
}

export function pickHomeIndexRepo(
  repos: PublicRepo[] | null | undefined,
  defaultRepoId: string | null | undefined,
): PublicRepo | null {
  if (!repos || repos.length === 0) {
    return null;
  }
  if (defaultRepoId) {
    const match = repos.find((repo) => repo.id === defaultRepoId);
    if (match) {
      return match;
    }
  }
  return repos[0] ?? null;
}

const INDEX_MODE_KEYS: Record<NonNullable<PublicRepo["index_mode"]>, MessageKey> = {
  sidecar: "index.sidecar",
  bind_mount: "index.bindMount",
  hosted_clone: "index.hostedClone",
  both: "index.both",
};

export function indexModeLabel(mode: PublicRepo["index_mode"] | undefined): string {
  if (!mode) {
    return t("index.notConnected");
  }
  return t(INDEX_MODE_KEYS[mode] ?? "index.notConnected");
}

export function connectionLabel(connected: boolean | undefined): string {
  return connected ? t("index.connected") : t("index.offline");
}

export function formatIndexWhen(value: string | null | undefined): string {
  if (!value) {
    return t("common.never");
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleString();
}

export function repoDisplayName(repo: Pick<PublicRepo, "remote_url" | "local_root_hint">): string {
  return repo.remote_url || repo.local_root_hint || t("common.repository");
}
