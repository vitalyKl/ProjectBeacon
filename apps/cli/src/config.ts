import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

import { resolveBeaconHome, resolveConfigPath } from "./home.js";

export const DEFAULT_BEACON_URL = "http://127.0.0.1:8080";

export type BeaconConfig = {
  url: string;
  token?: string;
  project_id?: string;
};

export type ProjectProfile = {
  token: string;
  url?: string;
};

export type ConfigFile = {
  values: Record<string, string>;
  projects: Record<string, ProjectProfile>;
};

export function emptyConfigFile(): ConfigFile {
  return { values: {}, projects: {} };
}

export function configFromValues(values: Record<string, string>): BeaconConfig {
  const url = values["url"]?.trim() || DEFAULT_BEACON_URL;
  const token = values["token"]?.trim() || undefined;
  const projectId = values["project_id"]?.trim() || undefined;
  return { url, token, project_id: projectId };
}

export function parseTomlScalars(source: string): Record<string, string> {
  return parseTomlDocument(source).values;
}

export function parseTomlDocument(source: string): {
  values: Record<string, string>;
  tables: Record<string, Record<string, string>>;
} {
  const values: Record<string, string> = {};
  const tables: Record<string, Record<string, string>> = {};
  let current = values;
  for (const rawLine of source.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }
    if (line.startsWith("[") && line.endsWith("]")) {
      const name = line.slice(1, -1).trim();
      tables[name] = tables[name] ?? {};
      current = tables[name];
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
      current[key] = value;
    }
  }
  return { values, tables };
}

export function projectsFromTables(
  tables: Record<string, Record<string, string>>,
): Record<string, ProjectProfile> {
  const projects: Record<string, ProjectProfile> = {};
  for (const [name, table] of Object.entries(tables)) {
    const projectId = parseProjectsTableName(name);
    const token = table["token"]?.trim();
    if (!projectId || !token) {
      continue;
    }
    const url = table["url"]?.trim();
    projects[projectId] = url ? { token, url } : { token };
  }
  return projects;
}

function parseProjectsTableName(name: string): string | undefined {
  const match = /^projects\.(?:"([^"]+)"|'([^']+)'|(.+))$/.exec(name.trim());
  const id = match?.[1] ?? match?.[2] ?? match?.[3];
  return id?.trim() || undefined;
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
  const projectIds = Object.keys(file.projects).sort();
  for (const projectId of projectIds) {
    const profile = file.projects[projectId];
    if (!profile?.token) {
      continue;
    }
    if (lines.length > 0) {
      lines.push("");
    }
    lines.push(`[projects.${quoteToml(projectId)}]`);
    lines.push(`token = ${quoteToml(profile.token)}`);
    if (profile.url) {
      lines.push(`url = ${quoteToml(profile.url)}`);
    }
  }
  return lines.length === 0 ? "" : `${lines.join("\n")}\n`;
}

function isKnownConfigKey(key: string): boolean {
  return key === "url" || key === "token" || key === "project_id";
}

export async function readConfigFile(path: string): Promise<ConfigFile> {
  try {
    const source = await readFile(path, "utf8");
    const parsed = parseTomlDocument(source);
    return { values: parsed.values, projects: projectsFromTables(parsed.tables) };
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
  if (process.platform !== "win32") {
    await chmod(path, 0o600);
  }
}

export function applyConfigPatch(file: ConfigFile, patch: Partial<BeaconConfig>): ConfigFile {
  const values = { ...file.values };
  const projects = { ...file.projects };
  if (patch.url !== undefined) {
    values["url"] = patch.url;
  }
  if (patch.token !== undefined) {
    values["token"] = patch.token;
  }
  if (patch.project_id !== undefined) {
    values["project_id"] = patch.project_id;
  }
  const projectId = patch.project_id?.trim() || values["project_id"]?.trim();
  const token = patch.token?.trim() || (projectId ? projects[projectId]?.token : undefined);
  if (projectId && token) {
    const url = patch.url?.trim() || projects[projectId]?.url || values["url"]?.trim();
    projects[projectId] = url ? { token, url } : { token };
  }
  return { values, projects };
}

export function mergeProjectProfiles(
  file: ConfigFile,
  extra: Record<string, ProjectProfile> = {},
): Record<string, ProjectProfile> {
  const projects = { ...file.projects, ...extra };
  const top = configFromValues(file.values);
  if (top.project_id && top.token && !projects[top.project_id]) {
    projects[top.project_id] = top.url ? { token: top.token, url: top.url } : { token: top.token };
  }
  return projects;
}

export type RuntimeConfig = BeaconConfig & {
  home: string;
  configPath: string;
  projects: Record<string, ProjectProfile>;
};

export function resolveRuntimeConfig(
  env: NodeJS.ProcessEnv = process.env,
  file: ConfigFile = emptyConfigFile(),
  requestedProjectId?: string,
): RuntimeConfig {
  const home = resolveBeaconHome(env);
  const fromFile = configFromValues(file.values);
  const projects = mergeProjectProfiles(file);
  const requested = requestedProjectId?.trim() || env["BEACON_PROJECT"]?.trim() || undefined;
  const projectId = requested || fromFile.project_id || Object.keys(projects).sort()[0];
  const profile = projectId ? projects[projectId] : undefined;
  const url = normalizeBeaconUrl(env["BEACON_URL"]?.trim() || profile?.url || fromFile.url);
  const token =
    profile?.token ??
    (Object.keys(projects).length === 0 || !requested || requested === fromFile.project_id
      ? fromFile.token
      : undefined);
  return {
    home,
    configPath: resolveConfigPath(home),
    url,
    token,
    project_id: projectId || undefined,
    projects,
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
