import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

import { resolveBeaconHome, resolveConfigPath } from "./home.js";

export const DEFAULT_BEACON_URL = "http://127.0.0.1:8080";

export type BeaconConfig = {
  url: string;
  token?: string;
  project_id?: string;
};

export type ConfigFile = {
  values: Record<string, string>;
};

export function emptyConfigFile(): ConfigFile {
  return { values: {} };
}

export function configFromValues(values: Record<string, string>): BeaconConfig {
  const url = values["url"]?.trim() || DEFAULT_BEACON_URL;
  const token = values["token"]?.trim() || undefined;
  const projectId = values["project_id"]?.trim() || undefined;
  return { url, token, project_id: projectId };
}

export function parseTomlScalars(source: string): Record<string, string> {
  const values: Record<string, string> = {};
  for (const rawLine of source.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#") || line.startsWith("[")) {
      continue;
    }
    const eq = line.indexOf("=");
    if (eq <= 0) {
      continue;
    }
    const key = line.slice(0, eq).trim();
    const raw = line.slice(eq + 1).trim();
    const value = unquoteToml(raw);
    if (key && value !== undefined) {
      values[key] = value;
    }
  }
  return values;
}

function unquoteToml(raw: string): string | undefined {
  if (
    (raw.startsWith('"') && raw.endsWith('"') && raw.length >= 2) ||
    (raw.startsWith("'") && raw.endsWith("'") && raw.length >= 2)
  ) {
    return raw.slice(1, -1);
  }
  if (raw.length === 0) {
    return undefined;
  }
  return raw;
}

function quoteToml(value: string): string {
  return `"${value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;
}

export function serializeConfigFile(file: ConfigFile): string {
  const keys = Object.keys(file.values);
  const ordered = ["url", "token", "project_id", ...keys.filter((key) => !isKnownConfigKey(key))];
  const seen = new Set<string>();
  const lines: string[] = [];
  for (const key of ordered) {
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    const value = file.values[key];
    if (value === undefined || value === "") {
      continue;
    }
    lines.push(`${key} = ${quoteToml(value)}`);
  }
  return lines.length === 0 ? "" : `${lines.join("\n")}\n`;
}

function isKnownConfigKey(key: string): boolean {
  return key === "url" || key === "token" || key === "project_id";
}

export async function readConfigFile(path: string): Promise<ConfigFile> {
  try {
    const source = await readFile(path, "utf8");
    return { values: parseTomlScalars(source) };
  } catch (error) {
    if (isNotFound(error)) {
      return emptyConfigFile();
    }
    throw error;
  }
}

export async function writeConfigFile(path: string, file: ConfigFile): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, serializeConfigFile(file), { encoding: "utf8", mode: 0o600 });
}

export function applyConfigPatch(file: ConfigFile, patch: Partial<BeaconConfig>): ConfigFile {
  const values = { ...file.values };
  if (patch.url !== undefined) {
    values["url"] = patch.url;
  }
  if (patch.token !== undefined) {
    values["token"] = patch.token;
  }
  if (patch.project_id !== undefined) {
    values["project_id"] = patch.project_id;
  }
  return { values };
}

export type RuntimeConfig = BeaconConfig & {
  home: string;
  configPath: string;
};

export function resolveRuntimeConfig(
  env: NodeJS.ProcessEnv = process.env,
  file: ConfigFile = emptyConfigFile(),
): RuntimeConfig {
  const home = resolveBeaconHome(env);
  const fromFile = configFromValues(file.values);
  const url = normalizeBeaconUrl(env["BEACON_URL"]?.trim() || fromFile.url);
  const projectId = env["BEACON_PROJECT"]?.trim() || fromFile.project_id;
  return {
    home,
    configPath: resolveConfigPath(home),
    url,
    token: fromFile.token,
    project_id: projectId || undefined,
  };
}

export function normalizeBeaconUrl(raw: string): string {
  const trimmed = raw.trim().replace(/\/+$/, "");
  if (!trimmed) {
    return DEFAULT_BEACON_URL;
  }
  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(trimmed)) {
    return trimmed;
  }
  const host = trimmed;
  if (host.startsWith("localhost") || host.startsWith("127.0.0.1") || host.startsWith("[::1]")) {
    return `http://${host}`;
  }
  return `https://${host}`;
}

function isNotFound(error: unknown): boolean {
  return Boolean(error && typeof error === "object" && "code" in error && error.code === "ENOENT");
}
