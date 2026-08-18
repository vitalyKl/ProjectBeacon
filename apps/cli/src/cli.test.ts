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
    expect(out.stdout).toContain("connect <token>");
    expect(out.stdout).toContain("mcp");
    expect(out.stdout.toLowerCase()).not.toContain("device-flow");
    expect(out.stdout.toLowerCase()).not.toContain("indexer");
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
      `url = "http://127.0.0.1:8080"\ntoken = "${TOKEN}"\n`,
      "utf8",
    );
    let started = false;
    const ready = await runCli({
      argv: ["mcp"],
      env: { BEACON_HOME: home },
      io: capture().io,
      serve: async () => {
        started = true;
      },
    });
    expect(ready).toBe(0);
    expect(started).toBe(true);
  });
});
