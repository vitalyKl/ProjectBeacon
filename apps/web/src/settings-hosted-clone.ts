export type HostedCloneIndexMode = "sidecar" | "hosted_clone" | "both";

export type HostedCloneRepo = {
  id: string;
  provider: string;
  index_mode: string;
  installation_id: string | null;
};

export function hostedCloneVisible(flags: { hosted_clone?: boolean } | null | undefined): boolean {
  return flags?.hosted_clone === true;
}

export function hostedCloneEligible(repo: HostedCloneRepo | null | undefined): boolean {
  return Boolean(repo && repo.provider === "github" && repo.installation_id);
}

export function parseHostedCloneMode(value: string): HostedCloneIndexMode | undefined {
  if (value === "sidecar" || value === "hosted_clone" || value === "both") {
    return value;
  }
  return undefined;
}
