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

export type SettingsPatchResult =
  | { ok: true; patch: Record<string, unknown> | undefined }
  | { ok: false; message: string; reason: string };

export function parseProjectSettingsPatch(
  settings: unknown,
  env: NodeJS.ProcessEnv = process.env,
): SettingsPatchResult {
  if (settings === undefined) {
    return { ok: true, patch: undefined };
  }
  if (settings === null || typeof settings !== "object" || Array.isArray(settings)) {
    return { ok: false, message: "invalid settings", reason: "invalid_body" };
  }
  const record = settings as Record<string, unknown>;
  const keys = Object.keys(record);
  if (keys.some((key) => key !== "github")) {
    return { ok: false, message: "invalid settings", reason: "invalid_body" };
  }
  if (record["github"] === undefined) {
    return { ok: true, patch: {} };
  }
  const github = record["github"];
  if (github === null || typeof github !== "object" || Array.isArray(github)) {
    return { ok: false, message: "invalid github settings", reason: "invalid_body" };
  }
  const githubRecord = github as Record<string, unknown>;
  if (Object.keys(githubRecord).some((key) => key !== "issues")) {
    return { ok: false, message: "invalid github settings", reason: "invalid_body" };
  }
  const issues = githubRecord["issues"];
  if (issues === undefined) {
    return { ok: true, patch: { github: {} } };
  }
  if (issues === "off" || issues === "import") {
    return { ok: true, patch: { github: { issues } } };
  }
  if (issues === "two_way") {
    if (!isGithubTwoWayEnabled(env)) {
      return {
        ok: false,
        message: "github two-way write is not enabled",
        reason: "github_two_way_off",
      };
    }
    return {
      ok: false,
      message: "github two-way write is not implemented",
      reason: "github_two_way_unavailable",
    };
  }
  return { ok: false, message: "github.issues must be off or import", reason: "invalid_body" };
}
