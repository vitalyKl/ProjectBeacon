import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { parseConnectArgs } from "./cli.js";
import { connect } from "./connect.js";
import { parseTomlScalars } from "./config.js";

const TOKEN = "bcn_" + "A".repeat(43);
const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";

function mockFetch(
  status: number,
  body: unknown = { id: PROJECT_ID, name: "Beacon" },
): typeof fetch {
  return (async () =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "content-type": "application/json" },
    })) as typeof fetch;
}

describe("beacon connect", () => {
  it("stores token and project after proving project:read", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const calls: string[] = [];
    const fetchImpl = (async (input: string | URL) => {
      calls.push(String(input));
      return new Response(JSON.stringify({ id: PROJECT_ID, name: "Beacon" }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }) as typeof fetch;

    const result = await connect({
      token: TOKEN,
      projectId: PROJECT_ID,
      env: { BEACON_HOME: home, BEACON_URL: "http://127.0.0.1:8080" },
      fetchImpl,
    });
    expect(result.ok).toBe(true);
    expect(calls[0]).toContain(`/v1/projects/${PROJECT_ID}`);
    const saved = parseTomlScalars(await readFile(join(home, "config.toml"), "utf8"));
    expect(saved["token"]).toBe(TOKEN);
    expect(saved["project_id"]).toBe(PROJECT_ID);
    expect(saved["url"]).toBe("http://127.0.0.1:8080");
    const raw = await readFile(join(home, "config.toml"), "utf8");
    expect(raw).toContain(`[projects."${PROJECT_ID}"]`);
  });

  it("rejects a malformed token", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const result = await connect({
      token: "not-a-token",
      projectId: PROJECT_ID,
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
      projectId: PROJECT_ID,
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

  it("requires a token and project", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-connect-"));
    const missingToken = await connect({ token: undefined, env: { BEACON_HOME: home } });
    expect(missingToken.ok).toBe(false);
    if (missingToken.ok) {
      throw new Error("expected failure");
    }
    expect(missingToken.message).toBe("Usage: beacon connect <token> --project <project-id>");

    const missingProject = await connect({
      token: TOKEN,
      env: { BEACON_HOME: home },
    });
    expect(missingProject.ok).toBe(false);
    if (missingProject.ok) {
      throw new Error("expected failure");
    }
    expect(missingProject.message).toContain("--project");
  });

  it("parses flags before the token", () => {
    expect(
      parseConnectArgs(["--url", "http://127.0.0.1:8080", "--project", PROJECT_ID, TOKEN]),
    ).toEqual({
      token: TOKEN,
      url: "http://127.0.0.1:8080",
      projectId: PROJECT_ID,
    });
    expect(parseConnectArgs([TOKEN, "--project", PROJECT_ID])).toEqual({
      token: TOKEN,
      url: undefined,
      projectId: PROJECT_ID,
    });
  });
});
