import { readFileSync } from "node:fs";
import { mkdtemp, mkdir, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import type { SetupPrompt } from "./prompt.js";
import {
  clientConfigTargets,
  parseSetupArgs,
  parseSetupClients,
  resolveRepoRoot,
  resolveSetupCredentials,
  setupMachine,
  upsertJsonMcpServer,
  upsertTomlTable,
} from "./setup.js";

const TOKEN = "bcn_" + "A".repeat(43);
const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";

function mockFetch(): typeof fetch {
  return (async () =>
    new Response(JSON.stringify({ id: PROJECT_ID, name: "Beacon" }), {
      status: 200,
      headers: { "content-type": "application/json" },
    })) as typeof fetch;
}

describe("beacon setup", () => {
  it("parses setup flags and known clients", () => {
    expect(
      parseSetupArgs([
        "--token",
        TOKEN,
        "--project",
        PROJECT_ID,
        "--url",
        "http://127.0.0.1:8080",
        "--client",
        "grok,cursor",
      ]),
    ).toEqual({
      token: TOKEN,
      url: "http://127.0.0.1:8080",
      projectId: PROJECT_ID,
      cwd: undefined,
      clients: "grok,cursor",
    });
    expect(parseSetupClients(undefined)).toEqual(["grok", "cursor", "claude"]);
    expect(parseSetupClients("grok")).toEqual(["grok"]);
    expect(parseSetupClients("nope")).toMatchObject({ error: expect.stringContaining("Unknown client") });
  });

  it("replaces a named TOML table and upserts JSON MCP servers", () => {
    const toml = upsertTomlTable(
      `[other]\nvalue = "keep"\n\n[mcp_servers.beacon]\ncommand = "old"\n`,
      "[mcp_servers.beacon]",
      `[mcp_servers.beacon]\ncommand = "new"\n`,
    );
    expect(toml).toContain('command = "new"');
    expect(toml).toContain("[other]");
    expect(toml).not.toContain('command = "old"');
    const glued = upsertTomlTable(
      `[mcp_servers.beacon]\ncommand = "old"\nenabled = true\nstartup_timeout_sec = 60command = "old"\n`,
      "[mcp_servers.beacon]",
      `# Written by beacon setup. Token stays in BEACON_HOME/config.toml.\n[mcp_servers.beacon]\ncommand = "new"\nenabled = true\nstartup_timeout_sec = 60\n`,
    );
    expect(glued).toContain('command = "new"');
    expect(glued).toContain("startup_timeout_sec = 60\n");
    expect(glued).not.toContain("60command");
    const replacement = `# Written by beacon setup. Token stays in BEACON_HOME/config.toml.\n[mcp_servers.beacon]\ncommand = "new"\nenabled = true\nstartup_timeout_sec = 60\n`;
    const twice = upsertTomlTable(glued, "[mcp_servers.beacon]", replacement);
    expect(twice.match(/\[mcp_servers\.beacon\]/g)).toHaveLength(1);
    const json = upsertJsonMcpServer(`{"mcpServers":{"other":{"command":"keep"}}}`, {
      command: "node",
      args: ["/tmp/mcp.cjs"],
    });
    expect(json.changed).toBe(true);
    expect(JSON.parse(json.text)).toMatchObject({
      mcpServers: {
        other: { command: "keep" },
        beacon: { command: "node", args: ["/tmp/mcp.cjs"] },
      },
    });
  });

  it("connects, writes a home launcher, and patches client configs without storing the token there", async () => {
    const beaconHome = await mkdtemp(join(tmpdir(), "beacon-setup-home-"));
    const userHome = await mkdtemp(join(tmpdir(), "beacon-setup-user-"));
    const repo = await mkdtemp(join(tmpdir(), "beacon-setup-repo-"));
    await mkdir(join(repo, "apps", "cli", "src"), { recursive: true });
    await mkdir(join(repo, "node_modules", "tsx", "dist"), { recursive: true });
    await writeFile(join(repo, "pnpm-workspace.yaml"), "packages: []\n");
    await writeFile(join(repo, "apps", "cli", "src", "index.ts"), "export {}\n");
    await writeFile(join(repo, "node_modules", "tsx", "dist", "cli.mjs"), "export {}\n");

    const result = await setupMachine({
      token: TOKEN,
      projectId: PROJECT_ID,
      cwd: repo,
      clients: "grok,cursor,claude",
      env: { BEACON_HOME: beaconHome, BEACON_URL: "http://127.0.0.1:8080" },
      fetchImpl: mockFetch(),
      homeDir: userHome,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) {
      throw new Error("expected setup to succeed");
    }

    const launcher = await readFile(join(beaconHome, "mcp.cjs"), "utf8");
    expect(launcher).toContain("apps");
    expect(launcher).toContain("mcp");
    expect(launcher).toMatch(/node_modules\/tsx\/dist\/cli\.mjs/);
    expect(launcher).not.toContain(TOKEN);

    const grok = await readFile(join(userHome, ".grok", "config.toml"), "utf8");
    expect(grok).toContain("[mcp_servers.beacon]");
    expect(grok).toContain("mcp.cjs");
    expect(grok).not.toContain(TOKEN);

    const cursor = JSON.parse(await readFile(join(userHome, ".cursor", "mcp.json"), "utf8")) as {
      mcpServers: { beacon: { args: string[] } };
    };
    expect(cursor.mcpServers.beacon.args[0]).toContain("mcp.cjs");

    const claude = JSON.parse(
      await readFile(join(userHome, ".claude", "claude_desktop_config.json"), "utf8"),
    ) as { mcpServers: { beacon: { command: string } } };
    expect(claude.mcpServers.beacon.command.length).toBeGreaterThan(0);
    expect(result.message).toContain("Wrote Beacon MCP");
    expect(result.files.some((file) => file.path.endsWith("config.toml"))).toBe(true);
    expect(result.files.some((file) => file.path.endsWith("mcp.json"))).toBe(true);
  });

  it("walks up from apps/cli to the checkout and writes Claude Code plus Desktop configs", async () => {
    const beaconHome = await mkdtemp(join(tmpdir(), "beacon-setup-walk-home-"));
    const userHome = await mkdtemp(join(tmpdir(), "beacon-setup-walk-user-"));
    const repo = await mkdtemp(join(tmpdir(), "beacon-setup-walk-repo-"));
    await mkdir(join(repo, "apps", "cli", "src"), { recursive: true });
    await mkdir(join(repo, "apps", "cli", "node_modules", "tsx", "dist"), { recursive: true });
    await writeFile(join(repo, "pnpm-workspace.yaml"), "packages: []\n");
    await writeFile(join(repo, "apps", "cli", "src", "index.ts"), "export {}\n");
    await writeFile(join(repo, "apps", "cli", "node_modules", "tsx", "dist", "cli.mjs"), "export {}\n");

    expect(resolveRepoRoot(join(repo, "apps", "cli"), {})).toBe(resolve(repo));

    const roaming = join(userHome, "AppData", "Roaming");
    const result = await setupMachine({
      token: TOKEN,
      projectId: PROJECT_ID,
      cwd: join(repo, "apps", "cli"),
      clients: "claude",
      env: {
        BEACON_HOME: beaconHome,
        BEACON_URL: "http://127.0.0.1:8080",
        APPDATA: roaming,
      },
      fetchImpl: mockFetch(),
      homeDir: userHome,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) {
      throw new Error("expected setup to succeed");
    }
    const launcher = await readFile(join(beaconHome, "mcp.cjs"), "utf8");
    expect(launcher).toContain(JSON.stringify(resolve(repo).split("\\").join("/")));
    expect(launcher).not.toContain("/apps/cli/apps/cli");

    const claudeCode = JSON.parse(await readFile(join(userHome, ".claude.json"), "utf8")) as {
      mcpServers: { beacon: { args: string[] } };
    };
    expect(claudeCode.mcpServers.beacon.args[0]).toContain("mcp.cjs");
    const desktop = JSON.parse(
      await readFile(join(roaming, "Claude", "claude_desktop_config.json"), "utf8"),
    ) as { mcpServers: { beacon: { command: string } } };
    expect(desktop.mcpServers.beacon.command.length).toBeGreaterThan(0);

    const targets = clientConfigTargets("claude", userHome, { APPDATA: roaming }).map(
      (target) => target.path,
    );
    expect(targets).toContain(join(userHome, ".claude.json"));
    expect(targets).toContain(join(roaming, "Claude", "claude_desktop_config.json"));
  });

  it("refuses to run without a token when there is no console", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-setup-missing-"));
    const result = await setupMachine({
      env: { BEACON_HOME: home },
      homeDir: home,
    });
    expect(result.ok).toBe(false);
    if (result.ok) {
      throw new Error("expected failure");
    }
    expect(result.message).toContain("--token");
    expect(result.message).toContain("paste");
    expect(result.message.toLowerCase()).not.toContain("hosted coding agent is available");
  });

  it("collects a hidden token and project from the console prompt", async () => {
    const asked: string[] = [];
    const prompt: SetupPrompt = {
      async line(question, defaultValue) {
        asked.push(question);
        if (question.startsWith("Project id")) {
          return PROJECT_ID;
        }
        if (question.startsWith("Control plane URL")) {
          return defaultValue ?? "http://127.0.0.1:8080";
        }
        return "";
      },
      async secret(question) {
        asked.push(question);
        return TOKEN;
      },
    };
    const resolved = await resolveSetupCredentials({
      runtime: { url: "http://127.0.0.1:8080" },
      prompt,
    });
    expect(resolved.ok).toBe(true);
    if (!resolved.ok) {
      throw new Error("expected credentials");
    }
    expect(resolved.credentials).toEqual({
      token: TOKEN,
      projectId: PROJECT_ID,
      url: "http://127.0.0.1:8080",
    });
    expect(asked.some((item) => item.toLowerCase().includes("token"))).toBe(true);
    expect(asked.some((item) => item.toLowerCase().includes("project"))).toBe(true);
  });

  it("uses a prompted token to finish setup without echoing it into agent configs", async () => {
    const beaconHome = await mkdtemp(join(tmpdir(), "beacon-setup-prompt-home-"));
    const userHome = await mkdtemp(join(tmpdir(), "beacon-setup-prompt-user-"));
    const repo = await mkdtemp(join(tmpdir(), "beacon-setup-prompt-repo-"));
    await mkdir(join(repo, "apps", "cli", "src"), { recursive: true });
    await mkdir(join(repo, "apps", "cli", "node_modules", "tsx", "dist"), { recursive: true });
    await writeFile(join(repo, "pnpm-workspace.yaml"), "packages: []\n");
    await writeFile(join(repo, "apps", "cli", "src", "index.ts"), "export {}\n");
    await writeFile(join(repo, "apps", "cli", "node_modules", "tsx", "dist", "cli.mjs"), "export {}\n");

    const result = await setupMachine({
      cwd: repo,
      clients: "grok",
      env: { BEACON_HOME: beaconHome, BEACON_URL: "http://127.0.0.1:8080" },
      fetchImpl: mockFetch(),
      homeDir: userHome,
      prompt: {
        async line(question) {
          if (question.startsWith("Project id")) {
            return PROJECT_ID;
          }
          return "http://127.0.0.1:8080";
        },
        async secret() {
          return TOKEN;
        },
      },
    });
    expect(result.ok).toBe(true);
    if (!result.ok) {
      throw new Error("expected setup to succeed");
    }
    const grok = await readFile(join(userHome, ".grok", "config.toml"), "utf8");
    expect(grok).toContain("[mcp_servers.beacon]");
    expect(grok).not.toContain(TOKEN);
    const saved = await readFile(join(beaconHome, "config.toml"), "utf8");
    expect(saved).toContain(TOKEN);
  });

  it("stops when the console token prompt is cancelled", async () => {
    const resolved = await resolveSetupCredentials({
      runtime: { url: "http://127.0.0.1:8080" },
      prompt: {
        async line() {
          return "";
        },
        async secret() {
          throw new Error("cancelled");
        },
      },
    });
    expect(resolved.ok).toBe(false);
    if (resolved.ok) {
      throw new Error("expected cancel");
    }
    expect(resolved.exitCode).toBe(130);
    expect(resolved.message).toContain("cancelled");
  });

  it("repo launchers collect a token in the console before beacon setup", () => {
    const root = join(dirname(fileURLToPath(import.meta.url)), "../../..");
    const ps1 = readFileSync(join(root, "setup.ps1"), "utf8");
    const cmd = readFileSync(join(root, "setup.cmd"), "utf8");
    const sh = readFileSync(join(root, "setup.sh"), "utf8");
    expect(ps1).toContain("start -- setup");
    expect(ps1).toContain("--cwd");
    expect(ps1).toContain("BEACON_SETUP_TOKEN");
    expect(sh).toContain("--cwd");
    expect(sh).toContain("BEACON_SETUP_TOKEN");
    expect(ps1).toContain("Read-Host");
    expect(ps1).toContain("-AsSecureString");
    expect(ps1).toContain("Press Enter to close");
    expect(cmd).toContain("setup.ps1");
    expect(cmd.toLowerCase()).toContain("pause");
    expect(sh).toContain("start -- setup");
    expect(sh).toContain("read_secret");
    expect(ps1.toLowerCase()).toContain("paste");
    expect(sh.toLowerCase()).toContain("paste");
    expect(ps1).not.toMatch(/bcn_[A-Za-z0-9]/);
    expect(cmd).not.toMatch(/bcn_[A-Za-z0-9]/);
    expect(sh).not.toMatch(/bcn_[A-Za-z0-9]/);
  });
});
