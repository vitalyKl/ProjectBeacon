import { existsSync } from "node:fs";
import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join, resolve, sep } from "node:path";

import { connect } from "./connect.js";
import { readConfigFile, resolveRuntimeConfig } from "./config.js";
import type { SetupPrompt } from "./prompt.js";
import { isProjectTokenFormat } from "./token.js";

export const SETUP_USAGE =
  "Usage: beacon setup [--token <token>] [--project <id>] [--url <url>] [--cwd <dir>] [--client grok,cursor,claude,opencode]";

export const SETUP_PROMPT_HINT =
  "With a terminal, omit --token and paste the project token when asked. Setup does not invent a token.";

export const BEACON_MCP_SERVER_NAME = "beacon";
export const DEFAULT_SETUP_CLIENTS = ["grok", "cursor", "claude", "opencode"] as const;

export type SetupClient = (typeof DEFAULT_SETUP_CLIENTS)[number];

export type SetupOptions = {
  token?: string;
  url?: string;
  projectId?: string;
  cwd?: string;
  clients?: string;
  env?: NodeJS.ProcessEnv;
  fetchImpl?: typeof fetch;
  homeDir?: string;
  prompt?: SetupPrompt;
};

export type SetupCredentials = {
  token: string;
  projectId: string;
  url: string;
};

export type SetupFileChange = {
  path: string;
  action: "created" | "updated" | "unchanged" | "skipped";
  detail: string;
};

export type SetupResult =
  | { ok: true; exitCode: 0; message: string; files: SetupFileChange[] }
  | { ok: false; exitCode: number; message: string; files: SetupFileChange[] };

export function parseSetupArgs(args: string[]): {
  token?: string;
  url?: string;
  projectId?: string;
  cwd?: string;
  clients?: string;
} {
  let token: string | undefined;
  let url: string | undefined;
  let projectId: string | undefined;
  let cwd: string | undefined;
  let clients: string | undefined;
  const positionals: string[] = [];
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (!arg) {
      continue;
    }
    if (arg === "--token" || arg.startsWith("--token=")) {
      token = arg === "--token" ? args[++i] : arg.slice("--token=".length);
      continue;
    }
    if (arg === "--url" || arg.startsWith("--url=")) {
      url = arg === "--url" ? args[++i] : arg.slice("--url=".length);
      continue;
    }
    if (arg === "--project" || arg.startsWith("--project=")) {
      projectId = arg === "--project" ? args[++i] : arg.slice("--project=".length);
      continue;
    }
    if (arg === "--cwd" || arg.startsWith("--cwd=")) {
      cwd = arg === "--cwd" ? args[++i] : arg.slice("--cwd=".length);
      continue;
    }
    if (arg === "--client" || arg.startsWith("--client=")) {
      clients = arg === "--client" ? args[++i] : arg.slice("--client=".length);
      continue;
    }
    if (arg === "--clients" || arg.startsWith("--clients=")) {
      clients = arg === "--clients" ? args[++i] : arg.slice("--clients=".length);
      continue;
    }
    if (arg.startsWith("-")) {
      continue;
    }
    positionals.push(arg);
  }
  return { token: token ?? positionals[0], url, projectId, cwd, clients };
}

export function parseSetupClients(raw: string | undefined): SetupClient[] | { error: string } {
  if (!raw || raw.trim().length === 0) {
    return [...DEFAULT_SETUP_CLIENTS];
  }
  const wanted = raw
    .split(",")
    .map((item) => item.trim().toLowerCase())
    .filter((item) => item.length > 0);
  const unique: SetupClient[] = [];
  for (const item of wanted) {
    if (item !== "grok" && item !== "cursor" && item !== "claude" && item !== "opencode") {
      return { error: `Unknown client: ${item}. Use grok, cursor, claude, and opencode.` };
    }
    if (!unique.includes(item)) {
      unique.push(item);
    }
  }
  if (unique.length === 0) {
    return [...DEFAULT_SETUP_CLIENTS];
  }
  return unique;
}

export function posixPath(path: string): string {
  return path.split(sep).join("/");
}

export function quoteToml(value: string): string {
  return `"${value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;
}

export function resolveTsxCli(repoRoot: string): string | undefined {
  const root = resolve(repoRoot);
  const candidates = [
    join(root, "apps", "cli", "node_modules", "tsx", "dist", "cli.mjs"),
    join(root, "node_modules", "tsx", "dist", "cli.mjs"),
  ];
  return candidates.find((path) => existsSync(path));
}

export function mcpLauncherSource(repoRoot: string, tsxCli = resolveTsxCli(repoRoot)): string {
  const root = posixPath(resolve(repoRoot));
  const tsx = tsxCli ? posixPath(resolve(tsxCli)) : "";
  return `#!/usr/bin/env node
"use strict";

const { spawn } = require("node:child_process");
const { existsSync } = require("node:fs");
const { join } = require("node:path");

const repo = ${JSON.stringify(root)};
const cli = join(repo, "apps", "cli", "src", "index.ts");
const tsx = ${JSON.stringify(tsx)};
if (!tsx || !existsSync(cli) || !existsSync(tsx)) {
  process.stderr.write(
    "beacon setup: ProjectBeacon checkout or tsx is missing. Run pnpm install, then re-run setup.\\n",
  );
  process.exit(1);
}

const child = spawn(process.execPath, [tsx, cli, "mcp"], {
  cwd: repo,
  env: process.env,
  stdio: "inherit",
});
child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 1);
});
`;
}

export function launcherPath(home: string): string {
  return join(home, "mcp.cjs");
}

export function grokMcpSnippet(command: string, args: readonly string[]): string {
  const argList = args.map((arg) => quoteToml(arg)).join(", ");
  return [
    `# Written by beacon setup. Token stays in BEACON_HOME/config.toml.`,
    `[mcp_servers.${BEACON_MCP_SERVER_NAME}]`,
    `command = ${quoteToml(command)}`,
    `args = [${argList}]`,
    `enabled = true`,
    `startup_timeout_sec = 60`,
    "",
  ].join("\n");
}

export function isTomlTableHeader(line: string): boolean {
  const trimmed = line.trim();
  return trimmed.startsWith("[") && trimmed.endsWith("]");
}

export function upsertTomlTable(source: string, tableHeader: string, block: string): string {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const header = tableHeader.trim();
  const headerIndex = lines.findIndex((line) => line.trim() === header);
  const nextBlock = `${block.replace(/\s*$/, "")}\n`;
  if (headerIndex < 0) {
    const body = source.replace(/\s*$/, "");
    return body.length === 0 ? nextBlock : `${body}\n\n${nextBlock}`;
  }
  let start = headerIndex;
  while (start > 0 && /^\s*#/.test(lines[start - 1] ?? "")) {
    start -= 1;
  }
  let end = headerIndex + 1;
  while (end < lines.length && !isTomlTableHeader(lines[end] ?? "")) {
    end += 1;
  }
  const before = lines.slice(0, start).join("\n").replace(/\s*$/, "");
  const after = lines.slice(end).join("\n").replace(/^\s*/, "").replace(/\s*$/, "");
  if (before.length === 0) {
    return after.length === 0 ? nextBlock : `${nextBlock}\n${after}\n`;
  }
  if (after.length === 0) {
    return `${before}\n\n${nextBlock}`;
  }
  return `${before}\n\n${nextBlock}\n${after}\n`;
}

export function upsertJsonMcpServer(
  source: string,
  server: { command: string; args: string[] },
): { text: string; changed: boolean } {
  const parsed = source.trim().length === 0 ? {} : (JSON.parse(source) as Record<string, unknown>);
  const root = parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  const mcpServers =
    root["mcpServers"] && typeof root["mcpServers"] === "object" && !Array.isArray(root["mcpServers"])
      ? { ...(root["mcpServers"] as Record<string, unknown>) }
      : {};
  const previous = mcpServers[BEACON_MCP_SERVER_NAME];
  const nextServer = { command: server.command, args: server.args };
  const same =
    previous &&
    typeof previous === "object" &&
    !Array.isArray(previous) &&
    (previous as { command?: unknown }).command === nextServer.command &&
    Array.isArray((previous as { args?: unknown }).args) &&
    JSON.stringify((previous as { args: unknown[] }).args) === JSON.stringify(nextServer.args);
  mcpServers[BEACON_MCP_SERVER_NAME] = nextServer;
  const next = { ...root, mcpServers };
  const text = `${JSON.stringify(next, null, 2)}\n`;
  return { text, changed: !same };
}

export function opencodeConfigPath(homeDir: string, env: NodeJS.ProcessEnv = process.env): string {
  const xdg = env["XDG_CONFIG_HOME"]?.trim() || join(homeDir, ".config");
  if (process.platform === "win32") {
    const appData = env["APPDATA"]?.trim() || join(homeDir, "AppData", "Roaming");
    const winPath = join(appData, "opencode", "opencode.json");
    if (existsSync(winPath)) {
      return winPath;
    }
  }
  return join(xdg, "opencode", "opencode.json");
}

export function opencodeConfigTargets(
  homeDir: string,
  env: NodeJS.ProcessEnv = process.env,
): { path: string; kind: "json" }[] {
  const path = opencodeConfigPath(homeDir, env);
  return [{ path, kind: "json" }];
}

export function upsertOpencodeMcpServer(
  source: string,
  name: string,
  server: { command: string; args: string[] },
): { text: string; changed: boolean } {
  const parsed = source.trim().length === 0 ? {} : (JSON.parse(source) as Record<string, unknown>);
  const root = parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  const mcp =
    root["mcp"] && typeof root["mcp"] === "object" && !Array.isArray(root["mcp"])
      ? { ...(root["mcp"] as Record<string, unknown>) }
      : {};
  const prev = mcp[name];
  const nextServer: Record<string, unknown> = {
    command: [server.command, ...server.args],
    enabled: true,
    type: "local",
  };
  const prevCmd =
    prev && typeof prev === "object" && !Array.isArray(prev)
      ? (prev as Record<string, unknown>)["command"] as string[] | string | undefined
      : undefined;
  const prevEnabled =
    prev && typeof prev === "object" && !Array.isArray(prev) ? (prev as Record<string, unknown>)["enabled"] : undefined;
  const prevType =
    prev && typeof prev === "object" && !Array.isArray(prev) ? (prev as Record<string, unknown>)["type"] : undefined;
  const same =
    prev &&
    typeof prev === "object" &&
    !Array.isArray(prev) &&
    prevType === "local" &&
    prevEnabled === true &&
    Array.isArray(prevCmd) &&
    JSON.stringify(prevCmd) === JSON.stringify([server.command, ...server.args]);
  mcp[name] = nextServer;
  const next = { ...root, mcp };
  const text = `${JSON.stringify(next, null, 2)}\n`;
  return { text, changed: !same };
}

export function isBeaconCheckout(root: string): boolean {
  return (
    existsSync(join(root, "pnpm-workspace.yaml")) &&
    existsSync(join(root, "apps", "cli", "src", "index.ts"))
  );
}

export function resolveRepoRoot(cwd: string, env: NodeJS.ProcessEnv): string {
  const fromEnv = env["BEACON_REPO"]?.trim();
  if (fromEnv) {
    return resolve(fromEnv);
  }
  let current = resolve(cwd);
  for (;;) {
    if (isBeaconCheckout(current)) {
      return current;
    }
    const parent = dirname(current);
    if (parent === current) {
      break;
    }
    current = parent;
  }
  return resolve(cwd);
}

export function claudeDesktopConfigPath(homeDir: string, env: NodeJS.ProcessEnv): string {
  if (process.platform === "win32") {
    const roaming = env["APPDATA"]?.trim() || join(homeDir, "AppData", "Roaming");
    return join(roaming, "Claude", "claude_desktop_config.json");
  }
  if (process.platform === "darwin") {
    return join(homeDir, "Library", "Application Support", "Claude", "claude_desktop_config.json");
  }
  const xdg = env["XDG_CONFIG_HOME"]?.trim() || join(homeDir, ".config");
  return join(xdg, "Claude", "claude_desktop_config.json");
}

export function clientConfigPath(
  client: SetupClient,
  homeDir: string,
  env: NodeJS.ProcessEnv = process.env,
): { path: string; kind: "toml" | "json" } {
  return clientConfigTargets(client, homeDir, env)[0]!;
}

export function clientConfigTargets(
  client: SetupClient,
  homeDir: string,
  env: NodeJS.ProcessEnv = process.env,
): { path: string; kind: "toml" | "json" }[] {
  if (client === "opencode") {
    return opencodeConfigTargets(homeDir, env);
  }
  if (client === "grok") {
    return [{ path: join(homeDir, ".grok", "config.toml"), kind: "toml" }];
  }
  if (client === "cursor") {
    return [{ path: join(homeDir, ".cursor", "mcp.json"), kind: "json" }];
  }
  if (client === "claude") {
    const targets = [
      { path: join(homeDir, ".claude.json"), kind: "json" as const },
      { path: join(homeDir, ".claude", "claude_desktop_config.json"), kind: "json" as const },
      { path: claudeDesktopConfigPath(homeDir, env), kind: "json" as const },
    ];
    const seen = new Set<string>();
    return targets.filter((target) => {
      const key = target.path.toLowerCase();
      if (seen.has(key)) {
        return false;
      }
      seen.add(key);
      return true;
    });
  }
  return [];
}

export function nodeInvocation(home: string): { command: string; args: string[] } {
  return { command: process.execPath, args: [launcherPath(home)] };
}

export async function resolveSetupCredentials(options: {
  token?: string;
  projectId?: string;
  url?: string;
  runtime: { token?: string; project_id?: string; url: string };
  prompt?: SetupPrompt;
}): Promise<
  | { ok: true; credentials: SetupCredentials }
  | { ok: false; exitCode: number; message: string }
> {
  let token = options.token?.trim() || options.runtime.token || "";
  let projectId = options.projectId?.trim() || options.runtime.project_id || "";
  let url = options.url?.trim() || options.runtime.url;
  let asked = false;

  try {
    if (!token && options.prompt) {
      token = (await options.prompt.secret("Paste project token (hidden, starts with bcn_)")).trim();
      asked = true;
    }
    if (!projectId && options.prompt) {
      projectId = (await options.prompt.line("Project id")).trim();
      asked = true;
    }
    if (asked && !options.url?.trim() && options.prompt) {
      const nextUrl = (await options.prompt.line("Control plane URL", options.runtime.url)).trim();
      if (nextUrl) {
        url = nextUrl;
      }
    }
  } catch (error) {
    if (error instanceof Error && error.message === "cancelled") {
      return { ok: false, exitCode: 130, message: "Setup cancelled." };
    }
    throw error;
  }

  if (!token) {
    return {
      ok: false,
      exitCode: 2,
      message: `${SETUP_USAGE}\nMint a project token in Agents, then paste it here or pass --token. ${SETUP_PROMPT_HINT}`,
    };
  }
  if (!isProjectTokenFormat(token)) {
    return {
      ok: false,
      exitCode: 2,
      message: "Token must be a project token minted in Beacon (starts with bcn_).",
    };
  }
  if (!projectId) {
    return {
      ok: false,
      exitCode: 2,
      message:
        "Project is required. Paste the project id, pass --project <id>, or run beacon connect first.",
    };
  }
  return { ok: true, credentials: { token, projectId, url } };
}

export async function setupMachine(options: SetupOptions): Promise<SetupResult> {
  const env = options.env ?? process.env;
  const cwd = resolve(options.cwd ?? process.cwd());
  const homeDir = options.homeDir ?? homedir();
  const clients = parseSetupClients(options.clients);
  if ("error" in clients) {
    return { ok: false, exitCode: 2, message: clients.error, files: [] };
  }

  const preview = resolveRuntimeConfig(env);
  const existing = await readConfigFile(preview.configPath);
  const runtime = resolveRuntimeConfig(env, existing);
  const resolved = await resolveSetupCredentials({
    token: options.token,
    projectId: options.projectId,
    url: options.url,
    runtime,
    prompt: options.prompt,
  });
  if (!resolved.ok) {
    return { ok: false, exitCode: resolved.exitCode, message: resolved.message, files: [] };
  }
  const { token, projectId, url } = resolved.credentials;

  const connected = await connect({
    token,
    url,
    projectId,
    env,
    fetchImpl: options.fetchImpl,
  });
  if (!connected.ok) {
    return { ok: false, exitCode: connected.exitCode, message: connected.message, files: [] };
  }

  const files: SetupFileChange[] = [];
  const repoRoot = resolveRepoRoot(cwd, env);
  if (!isBeaconCheckout(repoRoot)) {
    return {
      ok: false,
      exitCode: 2,
      message:
        "Could not find the ProjectBeacon checkout. Run setup.cmd / setup.sh from the repo, or pass --cwd <repo>.",
      files: [],
    };
  }
  const tsxCli = resolveTsxCli(repoRoot);
  if (!tsxCli) {
    return {
      ok: false,
      exitCode: 2,
      message: "tsx is missing in this checkout. Run pnpm install, then re-run setup.",
      files: [],
    };
  }
  const launcher = launcherPath(connected.config.home);
  files.push(await writeTextFile(launcher, mcpLauncherSource(repoRoot, tsxCli), 0o755));

  const invocation = nodeInvocation(connected.config.home);
  for (const client of clients) {
    for (const target of clientConfigTargets(client, homeDir, env)) {
      files.push(await writeClientConfig(target, invocation));
    }
  }

  const written = files.filter((file) => file.action === "created" || file.action === "updated");
  const configPaths = files
    .filter((file) => file.path !== launcher)
    .map((file) => `${file.action} ${file.path}`)
    .join("\n");
  const clientsList = clients.map((c) => c.charAt(0).toUpperCase() + c.slice(1)).join(", ");
  const message = [
    connected.message,
    `Local MCP launcher: ${launcher}`,
    written.length === 0
      ? "Agent MCP configs were already current."
      : `Wrote Beacon MCP into ${written.length} agent config file(s). Restart ${clientsList} so they reload MCP.`,
    configPaths,
    "The project token stays in BEACON_HOME/config.toml under [projects.\"<id>\"]. Agent configs only get the launcher command.",
    "Connect again for another Beacon project. mcp --project <id> or BEACON_PROJECT selects the default; tools may still pass project_id.",
    "Beacon does not run a hosted coding agent. HTTP MCP stays control-plane only.",
  ]
    .filter((line) => line.length > 0)
    .join("\n");
  return { ok: true, exitCode: 0, message, files };
}

async function writeClientConfig(
  target: { path: string; kind: "toml" | "json" },
  invocation: { command: string; args: string[] },
): Promise<SetupFileChange> {
  const existing = await readOptionalFile(target.path);
  if (target.kind === "toml") {
    const block = grokMcpSnippet(invocation.command, invocation.args);
    const next = upsertTomlTable(existing ?? "", `[mcp_servers.${BEACON_MCP_SERVER_NAME}]`, block);
    if (existing === next) {
      return { path: target.path, action: "unchanged", detail: "Grok MCP snippet already present." };
    }
    return writeTextFile(target.path, next, 0o600);
  }
  if (target.path.endsWith("opencode.json")) {
    try {
      const updated = upsertOpencodeMcpServer(existing ?? "", BEACON_MCP_SERVER_NAME, invocation);
      if (existing && !updated.changed) {
        return { path: target.path, action: "unchanged", detail: "MCP server already present." };
      }
      return writeTextFile(target.path, updated.text, 0o600);
    } catch {
      return {
        path: target.path,
        action: "skipped",
        detail: "Existing JSON is invalid; left untouched.",
      };
    }
  }
  try {
    const updated = upsertJsonMcpServer(existing ?? "", invocation);
    if (existing && !updated.changed) {
      return { path: target.path, action: "unchanged", detail: "MCP server already present." };
    }
    return writeTextFile(target.path, updated.text, 0o600);
  } catch {
    return {
      path: target.path,
      action: "skipped",
      detail: "Existing JSON is invalid; left untouched.",
    };
  }
}

async function writeTextFile(
  path: string,
  contents: string,
  mode: number,
): Promise<SetupFileChange> {
  const previous = await readOptionalFile(path);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, contents, { encoding: "utf8", mode });
  if (process.platform !== "win32") {
    await chmod(path, mode);
  }
  return {
    path,
    action: previous === undefined ? "created" : previous === contents ? "unchanged" : "updated",
    detail: posixPath(path),
  };
}

async function readOptionalFile(path: string): Promise<string | undefined> {
  try {
    return await readFile(path, "utf8");
  } catch (error) {
    if (error && typeof error === "object" && "code" in error && error.code === "ENOENT") {
      return undefined;
    }
    throw error;
  }
}
