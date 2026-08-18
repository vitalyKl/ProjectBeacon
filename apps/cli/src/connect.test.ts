import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { connect } from "./connect.js";
import { parseTomlScalars } from "./config.js";

const TOKEN = "bcn_" + "A".repeat(43);

function mockFetch(status: number, body: unknown = { items: [], next_cursor: null }): typeof fetch {
  return (async () =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "content-type": "application/json" },
    })) as typeof fetch;
}

describe("beacon connect", () => {
  it("stores a valid token without calling the API when no project is set", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const calls: string[] = [];
    const fetchImpl = (async (input: string | URL) => {
      calls.push(String(input));
      return new Response("{}", { status: 200 });
    }) as typeof fetch;

    const result = await connect({
      token: TOKEN,
      env: { BEACON_HOME: home, BEACON_URL: "http://127.0.0.1:8080" },
      fetchImpl,
    });
    expect(result.ok).toBe(true);
    expect(calls).toHaveLength(0);
    const saved = parseTomlScalars(await readFile(join(home, "config.toml"), "utf8"));
    expect(saved["token"]).toBe(TOKEN);
    expect(saved["url"]).toBe("http://127.0.0.1:8080");
  });

  it("rejects a malformed token", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const result = await connect({
      token: "not-a-token",
      env: { BEACON_HOME: home },
    });
    expect(result.ok).toBe(false);
    if (result.ok) {
      throw new Error("expected failure");
    }
    expect(result.message).toContain("bcn_");
    expect(result.message.toLowerCase()).not.toContain("device");
  });

  it("fails when the token lacks project:read", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const result = await connect({
      token: TOKEN,
      projectId: "01934567-89ab-7cde-89ab-0123456789ac",
      env: { BEACON_HOME: home, BEACON_URL: "http://127.0.0.1:8080" },
      fetchImpl: mockFetch(403, {
        error: { code: "forbidden", message: "insufficient token scope" },
      }),
    });
    expect(result.ok).toBe(false);
    if (result.ok) {
      throw new Error("expected failure");
    }
    expect(result.message).toContain("project:read");
  });

  it("requires a token argument", async () => {
    const result = await connect({
      token: undefined,
      env: { BEACON_HOME: await mkdtemp(join(tmpdir(), "beacon-connect-")) },
    });
    expect(result.ok).toBe(false);
    if (result.ok) {
      throw new Error("expected failure");
    }
    expect(result.message).toBe("Usage: beacon connect <token>");
  });
});
