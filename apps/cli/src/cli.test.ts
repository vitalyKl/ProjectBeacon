import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { runCli } from "./cli.js";

const TOKEN = "bcn_" + "A".repeat(43);

function capture() {
  let stdout = "";
  let stderr = "";
  return {
    io: {
      stdout: {
        write(chunk: string) {
          stdout += chunk;
        },
      },
      stderr: {
        write(chunk: string) {
          stderr += chunk;
        },
      },
    },
    get stdout() {
      return stdout;
    },
    get stderr() {
      return stderr;
    },
  };
}

describe("runCli", () => {
  it("prints product help", async () => {
    const out = capture();
    const code = await runCli({ argv: ["help"], io: out.io });
    expect(code).toBe(0);
    expect(out.stdout).toContain("connect <token> --project <id>");
    expect(out.stdout).toContain("BEACON_HOME");
    expect(out.stdout).toContain("BEACON_URL");
    expect(out.stdout).toContain("mcp");
    expect(out.stdout).toContain("sidecar");
    expect(out.stdout).toContain("setup");
    expect(out.stdout).toContain("projects");
    expect(out.stdout).toContain("[projects.");
    expect(out.stdout).toContain("writes Grok, Cursor, and Claude MCP configs");
    expect(out.stdout.toLowerCase()).not.toContain("device-flow");
    expect(out.stdout.toLowerCase()).not.toContain("indexer");
    expect(out.stdout.toLowerCase()).not.toContain("outbound tunnel");
    expect(out.stdout.toLowerCase()).not.toContain("wss://");
  });

  it("starts mcp only after connect", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-cli-"));
    const out = capture();
    const missing = await runCli({
      argv: ["mcp"],
      env: { BEACON_HOME: home },
      io: out.io,
      serve: async () => {
        throw new Error("should not start");
      },
    });
    expect(missing).toBe(1);
    expect(out.stderr).toContain("beacon connect");

    await writeFile(
      join(home, "config.toml"),
      `url = "http://127.0.0.1:8080"\ntoken = "${TOKEN}"\nproject_id = "01934567-89ab-7cde-89ab-0123456789ac"\n`,
      "utf8",
    );
    let started = false;
    let sidecar = false;
    const ready = await runCli({
      argv: ["mcp"],
      env: { BEACON_HOME: home },
      io: capture().io,
      serve: async () => {
        started = true;
      },
      startSidecar: async () => {
        sidecar = true;
        return {
          host: "127.0.0.1",
          port: 1,
          token: "t",
          close: async () => undefined,
        };
      },
    });
    expect(ready).toBe(0);
    expect(started).toBe(true);
    expect(sidecar).toBe(true);
  });

  it("does not start the sidecar from connect", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-cli-"));
    const projectId = "01934567-89ab-7cde-89ab-0123456789ac";
    const token = TOKEN;
    let sidecar = false;
    const out = capture();
    const code = await runCli({
      argv: ["connect", token, "--project", projectId],
      env: { BEACON_HOME: home, BEACON_URL: "http://127.0.0.1:8080" },
      io: out.io,
      fetchImpl: (async () =>
        new Response(JSON.stringify({ id: projectId, name: "Beacon" }), {
          status: 200,
          headers: { "content-type": "application/json" },
        })) as typeof fetch,
      startSidecar: async () => {
        sidecar = true;
        return {
          host: "127.0.0.1",
          port: 1,
          token: "t",
          close: async () => undefined,
        };
      },
    });
    expect(code).toBe(0);
    expect(sidecar).toBe(false);
    expect(out.stdout.toLowerCase()).toContain("connected");
  });

  it("setup without a token or TTY tells the user to paste one", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-cli-setup-"));
    const out = capture();
    const code = await runCli({
      argv: ["setup"],
      env: { BEACON_HOME: home },
      io: {
        ...out.io,
        stdin: { isTTY: false } as NodeJS.ReadStream,
      },
    });
    expect(code).toBe(2);
    expect(out.stderr).toContain("paste");
    expect(out.stderr).toContain("--token");
  });

  it("lists saved project ids from config.toml", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-cli-projects-"));
    const first = "01934567-89ab-7cde-89ab-0123456789ac";
    const second = "01934567-89ab-7cde-89ab-0123456789ad";
    await writeFile(
      join(home, "config.toml"),
      [
        `url = "http://127.0.0.1:8080"`,
        `token = "${TOKEN}"`,
        `project_id = "${second}"`,
        "",
        `[projects."${first}"]`,
        `token = "${TOKEN}"`,
        "",
        `[projects."${second}"]`,
        `token = "${TOKEN}"`,
        "",
      ].join("\n"),
      "utf8",
    );
    const out = capture();
    const code = await runCli({
      argv: ["projects"],
      env: { BEACON_HOME: home },
      io: out.io,
    });
    expect(code).toBe(0);
    expect(out.stdout).toContain(first);
    expect(out.stdout).toContain(`${second} *`);
  });

  it("eval --help prints usage with the three metrics", async () => {
    const out = capture();
    const code = await runCli({ argv: ["eval", "--help"], io: out.io });
    expect(code).toBe(0);
    expect(out.stdout).toContain("tokens_before_edit");
    expect(out.stdout).toContain("turns");
    expect(out.stdout).toContain("pass/fail");
  });
});
