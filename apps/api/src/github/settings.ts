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
  const currentWebhooks = current["webhooks"];
  const patchWebhooks = patch["webhooks"];
  if (
    currentWebhooks !== null &&
    typeof currentWebhooks === "object" &&
    !Array.isArray(currentWebhooks) &&
    patchWebhooks !== null &&
    typeof patchWebhooks === "object" &&
    !Array.isArray(patchWebhooks)
  ) {
    const currentWh = currentWebhooks as Record<string, unknown>;
    const patchWh = patchWebhooks as Record<string, unknown>;
    if (Array.isArray(currentWh["urls"]) && Array.isArray(patchWh["urls"])) {
      const mergedUrls = [...new Set([...currentWh["urls"], ...patchWh["urls"]])];
      next["webhooks"] = { ...currentWebhooks, ...patchWebhooks, urls: mergedUrls };
    } else {
      next["webhooks"] = { ...currentWebhooks, ...patchWebhooks };
    }
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
  const allowedKeys = ["github", "webhooks"];
  if (keys.some((key) => !allowedKeys.includes(key))) {
    return { ok: false, message: "invalid settings", reason: "invalid_body" };
  }
  const patch: Record<string, unknown> = {};
  if (record["webhooks"] !== undefined) {
    const result = parseWebhookSettings(record["webhooks"]);
    if (!result.ok) {
      return { ok: false, message: result.message, reason: "invalid_body" };
    }
    if (result.urls.length > 0) {
      patch["webhooks"] = { urls: result.urls };
    }
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
    return { ok: true, patch: { ...patch, github: {} } };
  }
  if (issues === "off" || issues === "import") {
    return { ok: true, patch: { ...patch, github: { issues } } };
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

export type WebhookConfig = {
  url: string;
  enabled: boolean;
};

export function isValidWebhookUrl(input: unknown): boolean {
  if (typeof input !== "string") return false;
  try {
    const url = new URL(input);
    return url.protocol === "https:";
  } catch {
    return false;
  }
}

export function parseWebhookSettings(
  settings: unknown,
): { urls: string[]; ok: true } | { ok: false; message: string } {
  if (settings === undefined || settings === null) {
    return { urls: [], ok: true };
  }
  if (typeof settings !== "object" || Array.isArray(settings)) {
    return { ok: false, message: "webhooks must be an object" };
  }
  const obj = settings as Record<string, unknown>;
  const rawUrls = obj["urls"];
  if (!Array.isArray(rawUrls)) {
    return { ok: false, message: "webhooks.urls must be an array" };
  }
  const urls: string[] = [];
  for (const item of rawUrls) {
    if (typeof item === "string" && isValidWebhookUrl(item)) {
      urls.push(item);
    }
  }
  return { urls, ok: true };
}
