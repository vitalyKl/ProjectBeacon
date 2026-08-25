import type { InvokeContext } from "@beacon/mcp-tools";

import { CONNECT_USAGE, connect } from "./connect.js";
import { readConfigFile, resolveRuntimeConfig } from "./config.js";
import { EVAL_HELP, EVAL_METRICS, runEvalCommand } from "./eval.js";
import { createLocalCodeSource } from "./local-code.js";
import { createSetupPrompt, isInteractiveIo, type PromptIo } from "./prompt.js";
import { parseSetupArgs, setupMachine } from "./setup.js";
import { startSidecar } from "./sidecar.js";
import { serveStdio } from "./stdio.js";

export const USAGE = `Usage: beacon <command>

Commands:
  connect <token> --project <id>   Save a project token minted in Beacon
  setup                            Prompt for token and write local MCP settings
  projects                         List saved project ids in BEACON_HOME
  mcp                              Start the stdio MCP server
  sidecar                          Start the local code index sidecar
  eval <fixture>                   Run context-effectiveness evaluation
  help                             Show this help

Options:
  --token <token>     Project token (or paste it when setup asks)
  --url <url>         Control plane URL (or BEACON_URL)
  --project <id>      Project id (or BEACON_PROJECT)
  --cwd <dir>         Checkout used by setup (or BEACON_REPO)
  --client <list>     setup clients: grok,cursor,claude

BEACON_HOME defaults to ~/.beacon (Unix) or %USERPROFILE%\\.beacon (Windows).
Connect again for each Beacon project. Tokens stay in BEACON_HOME/config.toml under [projects."<id>"].
mcp --project <id> or BEACON_PROJECT selects the default. Agents may still pass project_id on a tool.
setup writes Grok, Cursor, and Claude MCP configs. The token stays in BEACON_HOME. It does not mint tokens or start a hosted agent.

eval metrics: ${EVAL_METRICS.join(", ")}
`;

export type CliIo = {
  stdout: { write(chunk: string): void };
  stderr: { write(chunk: string): void };
  stdin?: PromptIo["stdin"];
};

export type RunCliOptions = {
  argv?: string[];
  env?: NodeJS.ProcessEnv;
  io?: CliIo;
  fetchImpl?: typeof fetch;
  serve?: typeof serveStdio;
  cwd?: string;
  startSidecar?: typeof startSidecar;
  prompt?: ReturnType<typeof createSetupPrompt>;
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

function parseProjectFlag(args: string[]): string | undefined {
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (!arg) {
      continue;
    }
    if (arg === "--project" || arg.startsWith("--project=")) {
      return arg === "--project" ? args[i + 1] : arg.slice("--project=".length);
    }
  }
  return undefined;
}

async function loadRuntime(env: NodeJS.ProcessEnv, requestedProjectId?: string) {
  const preview = resolveRuntimeConfig(env);
  const file = await readConfigFile(preview.configPath);
  return resolveRuntimeConfig(env, file, requestedProjectId);
}

async function buildInvokeContext(
  env: NodeJS.ProcessEnv,
  fetchImpl: typeof fetch | undefined,
  cwd: string,
  requestedProjectId?: string,
): Promise<{ ok: true; ctx: InvokeContext } | { ok: false; message: string }> {
  const runtime = await loadRuntime(env, requestedProjectId);
  const hasAnyProject = Boolean(runtime.token && runtime.project_id) || Object.keys(runtime.projects).length > 0;
  if (!hasAnyProject) {
    return {
      ok: false,
      message:
        "Not connected. Run beacon connect <token> --project <id> with a project token minted in Beacon.",
    };
  }
  if (requestedProjectId && !runtime.token) {
    return {
      ok: false,
      message: `No saved token for project ${requestedProjectId}. Run beacon connect <token> --project ${requestedProjectId}.`,
    };
  }
  return {
    ok: true,
    ctx: {
      baseUrl: runtime.url,
      token: runtime.token ?? "",
      projectId: runtime.project_id,
      projectTokens: Object.fromEntries(
        Object.entries(runtime.projects).map(([id, profile]) => [id, profile.token]),
      ),
      projectUrls: Object.fromEntries(
        Object.entries(runtime.projects)
          .filter((entry): entry is [string, { token: string; url: string }] => Boolean(entry[1].url))
          .map(([id, profile]) => [id, profile.url]),
      ),
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
  requestedProjectId?: string,
): Promise<void> {
  const runtime = await loadRuntime(env, requestedProjectId);
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
    });
  } catch {
    // sidecar is best-effort next to mcp
  }
}

export async function runCli(options: RunCliOptions = {}): Promise<number> {
  const argv = options.argv ?? process.argv.slice(2);
  const env = options.env ?? process.env;
  const io: CliIo = options.io ?? {
    stdout: process.stdout,
    stderr: process.stderr,
    stdin: process.stdin,
  };
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

  if (command === "setup") {
    const parsed = parseSetupArgs(rest);
    const promptIo: PromptIo = {
      stdin: io.stdin ?? process.stdin,
      stdout: io.stdout,
    };
    const prompt =
      options.prompt ?? (isInteractiveIo(promptIo) ? createSetupPrompt(promptIo) : undefined);
    const result = await setupMachine({
      token: parsed.token ?? env["BEACON_SETUP_TOKEN"] ?? env["BEACON_TOKEN"],
      url: parsed.url ?? env["BEACON_SETUP_URL"] ?? env["BEACON_URL"],
      projectId: parsed.projectId ?? env["BEACON_SETUP_PROJECT"] ?? env["BEACON_PROJECT"],
      cwd: parsed.cwd ?? env["BEACON_REPO"] ?? cwd,
      clients: parsed.clients,
      env,
      fetchImpl: options.fetchImpl,
      prompt,
    });
    if (result.ok) {
      io.stdout.write(`${result.message}\n`);
      return 0;
    }
    io.stderr.write(`${result.message}\n`);
    return result.exitCode;
  }

  if (command === "projects") {
    const runtime = await loadRuntime(env);
    const ids = Object.keys(runtime.projects);
    if (ids.length === 0 && !runtime.project_id) {
      io.stderr.write(
        "No saved projects. Run beacon connect <token> --project <id> with a project token minted in Beacon.\n",
      );
      return 1;
    }
    const listed = ids.length > 0 ? ids : runtime.project_id ? [runtime.project_id] : [];
    for (const id of listed) {
      const marker = id === runtime.project_id ? " *" : "";
      io.stdout.write(`${id}${marker}\n`);
    }
    return 0;
  }

  if (command === "mcp") {
    const requestedProjectId = parseProjectFlag(rest) ?? env["BEACON_PROJECT"];
    const loaded = await buildInvokeContext(env, options.fetchImpl, cwd, requestedProjectId);
    if (!loaded.ok) {
      io.stderr.write(`${loaded.message}\n`);
      return 1;
    }
    await autoStartSidecar(options, env, cwd, requestedProjectId);
    const serve = options.serve ?? serveStdio;
    await serve({ ctx: loaded.ctx });
    return 0;
  }

  if (command === "sidecar") {
    const runtime = await loadRuntime(env, parseProjectFlag(rest) ?? env["BEACON_PROJECT"]);
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
    });
    io.stdout.write(`Sidecar listening on ${started.host}:${started.port}\n`);
    return 0;
  }

  if (command === "eval") {
    if (rest.includes("--help") || rest.includes("-h")) {
      io.stdout.write(`${EVAL_HELP}\n`);
      return 0;
    }
    const fixturePath = rest.find((arg) => !arg.startsWith("--") && arg !== "eval");
    if (!fixturePath) {
      io.stderr.write(`${EVAL_HELP}\n`);
      return 2;
    }
    const format = rest.includes("--json") ? "json" : "text";
    const code = await runEvalCommand(
      { filePath: fixturePath, format },
      io,
    );
    return code;
  }

  io.stderr.write(`Unknown command: ${command}\n${USAGE}\n`);
  return 2;
}
