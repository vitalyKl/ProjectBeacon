import type { GithubIssuesMode } from "./types.js";

export function githubIssuesMode(settings: Record<string, unknown>): GithubIssuesMode {
  const github = settings["github"];
  if (github === null || typeof github !== "object" || Array.isArray(github)) {
    return "off";
  }
  const issues = (github as { issues?: unknown }).issues;
  return issues === "import" ? "import" : "off";
}

export function isGithubTwoWayEnabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return env["FF_GITHUB_TWO_WAY"] === "true";
}

export function mergeProjectSettings(
  current: Record<string, unknown>,
  patch: Record<string, unknown>,
): Record<string, unknown> {
  const next = { ...current, ...patch };
  const currentGithub = current["github"];
  const patchGithub = patch["github"];
  if (
    currentGithub !== null &&
    typeof currentGithub === "object" &&
    !Array.isArray(currentGithub) &&
    patchGithub !== null &&
    typeof patchGithub === "object" &&
    !Array.isArray(patchGithub)
  ) {
    next["github"] = { ...currentGithub, ...patchGithub };
  }
  return next;
}
