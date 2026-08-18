import type { InvokeContext } from "@beacon/mcp-tools";

import { unavailableCodeSource } from "./code-source.js";
import { CONNECT_USAGE, connect } from "./connect.js";
import { readConfigFile, resolveRuntimeConfig } from "./config.js";
import { serveStdio } from "./stdio.js";

export const USAGE = `Usage: beacon <command>

Commands:
  connect <token> --project <id>   Save a project token minted in Beacon
  mcp                              Start the stdio MCP server
  help                             Show this help

Options:
  --url <url>         Control plane URL (or BEACON_URL)
  --project <id>      Project id (or BEACON_PROJECT)

BEACON_HOME defaults to ~/.beacon (Unix) or %USERPROFILE%\\.beacon (Windows).`;

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
      codeSource: unavailableCodeSource(),
    },
  };
}

export async function runCli(options: RunCliOptions = {}): Promise<number> {
  const argv = options.argv ?? process.argv.slice(2);
  const env = options.env ?? process.env;
  const io = options.io ?? { stdout: process.stdout, stderr: process.stderr };
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
    const loaded = await buildInvokeContext(env, options.fetchImpl);
    if (!loaded.ok) {
      io.stderr.write(`${loaded.message}\n`);
      return 1;
    }
    const serve = options.serve ?? serveStdio;
    await serve({ ctx: loaded.ctx });
    return 0;
  }

  io.stderr.write(`Unknown command: ${command}\n${USAGE}\n`);
  return 2;
}
