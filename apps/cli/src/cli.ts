import type { InvokeContext } from "@beacon/mcp-tools";

import { CONNECT_USAGE, connect } from "./connect.js";
import { readConfigFile, resolveRuntimeConfig } from "./config.js";
import { createLocalCodeSource } from "./local-code.js";
import { startSidecar } from "./sidecar.js";
import { serveStdio } from "./stdio.js";

export const USAGE = `Usage: beacon <command>

Commands:
  connect <token> --project <id>   Save a project token minted in Beacon
  mcp                              Start the stdio MCP server
  sidecar                          Start the local code index sidecar
  help                             Show this help

Options:
  --url <url>         Control plane URL (or BEACON_URL)
  --project <id>      Project id (or BEACON_PROJECT)

BEACON_HOME defaults to ~/.beacon (Unix) or %USERPROFILE%\\.beacon (Windows).
BEACON_HOST opts the sidecar into the outbound tunnel (wss://$BEACON_HOST/v1/sidecar).`;

export type CliIo = {
  stdout: { write(chunk: string): void };
  stderr: { write(chunk: string): void };
};

export type RunCliOptions = {
  argv?: string[];
  env?: NodeJS.ProcessEnv;
  io?: CliIo;
  fetchImpl?: typeof fetch;
  serve?: typeof serveStdio;
  cwd?: string;
  startSidecar?: typeof startSidecar;
};

export function parseConnectArgs(args: string[]): {
  token?: string;
  url?: string;
  projectId?: string;
} {
  let url: string | undefined;
  let projectId: string | undefined;
  const positionals: string[] = [];
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (!arg) {
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
    if (arg.startsWith("-")) {
      continue;
    }
    positionals.push(arg);
  }
  return { token: positionals[0], url, projectId };
}

async function loadRuntime(env: NodeJS.ProcessEnv) {
  const preview = resolveRuntimeConfig(env);
  const file = await readConfigFile(preview.configPath);
  return resolveRuntimeConfig(env, file);
}

async function buildInvokeContext(
  env: NodeJS.ProcessEnv,
  fetchImpl: typeof fetch | undefined,
  cwd: string,
): Promise<{ ok: true; ctx: InvokeContext } | { ok: false; message: string }> {
  const runtime = await loadRuntime(env);
  if (!runtime.token || !runtime.project_id) {
    return {
      ok: false,
      message:
        "Not connected. Run beacon connect <token> --project <id> with a project token minted in Beacon.",
    };
  }
  return {
    ok: true,
    ctx: {
      baseUrl: runtime.url,
      token: runtime.token,
      projectId: runtime.project_id,
      fetch: fetchImpl,
      codeSource: createLocalCodeSource({
        cwd,
        home: runtime.home,
        map: { roots: {} },
      }),
    },
  };
}

async function autoStartSidecar(
  options: RunCliOptions,
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<void> {
  const runtime = await loadRuntime(env);
  if (!runtime.token || !runtime.project_id) {
    return;
  }
  const start = options.startSidecar ?? startSidecar;
  try {
    await start({
      home: runtime.home,
      cwd,
      url: runtime.url,
      token: runtime.token,
      projectId: runtime.project_id,
      fetchImpl: options.fetchImpl,
      beaconHost: env["BEACON_HOST"],
      tunnelEnabled: env["FF_SIDECAR_TUNNEL"] === "true" || env["ff.sidecar_tunnel"] === "true",
    });
  } catch {
    // sidecar is best-effort next to mcp
  }
}

export async function runCli(options: RunCliOptions = {}): Promise<number> {
  const argv = options.argv ?? process.argv.slice(2);
  const env = options.env ?? process.env;
  const io = options.io ?? { stdout: process.stdout, stderr: process.stderr };
  const cwd = options.cwd ?? process.cwd();
  const [command, ...rest] = argv;

  if (!command || command === "help" || command === "--help" || command === "-h") {
    io.stdout.write(`${USAGE}\n`);
    return 0;
  }

  if (command === "connect") {
    const parsed = parseConnectArgs(rest);
    const result = await connect({
      token: parsed.token,
      url: parsed.url ?? env["BEACON_URL"],
      projectId: parsed.projectId ?? env["BEACON_PROJECT"],
      env,
      fetchImpl: options.fetchImpl,
    });
    if (result.ok) {
      io.stdout.write(`${result.message}\n`);
      return 0;
    }
    io.stderr.write(`${result.message}\n`);
    if (result.message === CONNECT_USAGE) {
      io.stderr.write(
        "Mint a project token in Beacon, then run beacon connect <token> --project <id>.\n",
      );
    }
    return result.exitCode;
  }

  if (command === "mcp") {
    const loaded = await buildInvokeContext(env, options.fetchImpl, cwd);
    if (!loaded.ok) {
      io.stderr.write(`${loaded.message}\n`);
      return 1;
    }
    await autoStartSidecar(options, env, cwd);
    const serve = options.serve ?? serveStdio;
    await serve({ ctx: loaded.ctx });
    return 0;
  }

  if (command === "sidecar") {
    const runtime = await loadRuntime(env);
    if (!runtime.token || !runtime.project_id) {
      io.stderr.write(
        "Not connected. Run beacon connect <token> --project <id> with a project token minted in Beacon.\n",
      );
      return 1;
    }
    const start = options.startSidecar ?? startSidecar;
    const started = await start({
      home: runtime.home,
      cwd,
      url: runtime.url,
      token: runtime.token,
      projectId: runtime.project_id,
      fetchImpl: options.fetchImpl,
      beaconHost: env["BEACON_HOST"],
      tunnelEnabled: env["FF_SIDECAR_TUNNEL"] === "true" || env["ff.sidecar_tunnel"] === "true",
    });
    io.stdout.write(`Sidecar listening on ${started.host}:${started.port}\n`);
    return 0;
  }

  io.stderr.write(`Unknown command: ${command}\n${USAGE}\n`);
  return 2;
}
