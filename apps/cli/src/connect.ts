import { isUuid } from "@beacon/shared";

import { fetchProjectRead, isForbidden, isNotFound, isUnauthorized } from "./api.js";
import {
  applyConfigPatch,
  normalizeBeaconUrl,
  readConfigFile,
  resolveRuntimeConfig,
  writeConfigFile,
  type RuntimeConfig,
} from "./config.js";
import { isProjectTokenFormat } from "./token.js";

export const CONNECT_USAGE = "Usage: beacon connect <token> --project <project-id>";

export type ConnectOptions = {
  token?: string;
  url?: string;
  projectId?: string;
  env?: NodeJS.ProcessEnv;
  fetchImpl?: typeof fetch;
};

export type ConnectResult =
  | { ok: true; config: RuntimeConfig; message: string }
  | { ok: false; exitCode: number; message: string };

export async function connect(options: ConnectOptions): Promise<ConnectResult> {
  const token = options.token?.trim();
  if (!token) {
    return { ok: false, exitCode: 2, message: CONNECT_USAGE };
  }
  if (!isProjectTokenFormat(token)) {
    return {
      ok: false,
      exitCode: 2,
      message: "Token must be a project token minted in Beacon (starts with bcn_).",
    };
  }

  const env = options.env ?? process.env;
  const existing = await readConfigFile(resolveRuntimeConfig(env).configPath);
  const runtime = resolveRuntimeConfig(env, existing);
  const url = normalizeBeaconUrl(options.url?.trim() || runtime.url);
  const projectId = options.projectId?.trim() || runtime.project_id;
  if (!projectId) {
    return {
      ok: false,
      exitCode: 2,
      message: "Project is required. Pass --project <id> or set BEACON_PROJECT.",
    };
  }
  if (!isUuid(projectId)) {
    return { ok: false, exitCode: 2, message: "Project id must be a UUID." };
  }

  let projectLabel = projectId;
  try {
    const access = await fetchProjectRead(url, token, projectId, options.fetchImpl);
    if (!access.ok) {
      return mapConnectFailure(access.error);
    }
    projectLabel = access.project?.name ?? access.project?.slug ?? projectId;
  } catch {
    return {
      ok: false,
      exitCode: 1,
      message: `Could not reach ${url}. Check BEACON_URL and try again.`,
    };
  }

  const next = applyConfigPatch(existing, { url, token, project_id: projectId });
  await writeConfigFile(runtime.configPath, next);
  return {
    ok: true,
    config: { ...runtime, url, token, project_id: projectId },
    message: `Connected to ${projectLabel}. Token saved to ${runtime.configPath}.`,
  };
}

function mapConnectFailure(error: {
  status: number;
  code?: string;
  message: string;
}): ConnectResult {
  if (isUnauthorized(error)) {
    return { ok: false, exitCode: 1, message: "Token is invalid or expired." };
  }
  if (isForbidden(error)) {
    return {
      ok: false,
      exitCode: 1,
      message: "This token cannot read the project. Mint a token with project:read.",
    };
  }
  if (isNotFound(error)) {
    return { ok: false, exitCode: 1, message: "Project not found for this token." };
  }
  return { ok: false, exitCode: 1, message: error.message };
}
