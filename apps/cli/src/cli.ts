import type { InvokeContext } from "@beacon/mcp-tools";

import { unavailableCodeSource } from "./code-source.js";
import { CONNECT_USAGE, connect } from "./connect.js";
import { readConfigFile, resolveRuntimeConfig } from "./config.js";
import { serveStdio } from "./stdio.js";

export const USAGE = `Usage: beacon <command>

Commands:
  connect <token>   Save a project token minted in Beacon
  mcp               Start the stdio MCP server
  help              Show this help`;

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

function parseFlag(args: string[], name: string): string | undefined {
  const prefix = `${name}=`;
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (!arg) {
      continue;
    }
    if (arg === name) {
      return args[i + 1];
    }
    if (arg.startsWith(prefix)) {
      return arg.slice(prefix.length);
    }
  }
  return undefined;
}

function positionalToken(args: string[]): string | undefined {
  for (const arg of args) {
    if (!arg.startsWith("-")) {
      return arg;
    }
  }
  return undefined;
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
  if (!runtime.token) {
    return {
      ok: false,
      message: "Not connected. Run beacon connect <token> with a project token minted in Beacon.",
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
    const result = await connect({
      token: positionalToken(rest),
      url: parseFlag(rest, "--url") ?? env["BEACON_URL"],
      projectId: parseFlag(rest, "--project") ?? env["BEACON_PROJECT"],
      env,
      fetchImpl: options.fetchImpl,
    });
    if (result.ok) {
      io.stdout.write(`${result.message}\n`);
      return 0;
    }
    io.stderr.write(`${result.message}\n`);
    if (result.message === CONNECT_USAGE) {
      io.stderr.write("Mint a project token in Beacon, then run beacon connect <token>.\n");
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
