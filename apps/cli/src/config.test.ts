import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import {
  applyConfigPatch,
  normalizeBeaconUrl,
  parseTomlScalars,
  readConfigFile,
  resolveRuntimeConfig,
  serializeConfigFile,
  writeConfigFile,
} from "./config.js";
import { resolveBeaconHome } from "./home.js";

describe("config", () => {
  it("defaults BEACON_HOME to ~/.beacon and honors the override", () => {
    expect(resolveBeaconHome({})).toMatch(/\.beacon$/);
    expect(resolveBeaconHome({ BEACON_HOME: "C:\\tmp\\beacon-home" })).toBe("C:\\tmp\\beacon-home");
  });

  it("parses and serializes config.toml scalars", () => {
    const values = parseTomlScalars(`
# comment
url = "https://beacon.example"
token = "bcn_abcdefghijklmnopqrstuvwxyz0123456789ABC"
project_id = "01934567-89ab-7cde-89ab-0123456789ac"
`);
    expect(values).toEqual({
      url: "https://beacon.example",
      token: "bcn_abcdefghijklmnopqrstuvwxyz0123456789ABC",
      project_id: "01934567-89ab-7cde-89ab-0123456789ac",
    });
    expect(serializeConfigFile({ values })).toContain('url = "https://beacon.example"');
  });

  it("lets BEACON_URL override the file and prefixes hosts", () => {
    const runtime = resolveRuntimeConfig(
      { BEACON_HOME: "/tmp/beacon", BEACON_URL: "beacon.example", BEACON_PROJECT: "proj" },
      { values: { url: "http://127.0.0.1:8080", token: "tok" } },
    );
    expect(runtime.url).toBe("https://beacon.example");
    expect(runtime.project_id).toBe("proj");
    expect(normalizeBeaconUrl("127.0.0.1:9000")).toBe("http://127.0.0.1:9000");
  });

  it("writes token to BEACON_HOME/config.toml", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-cli-"));
    const path = join(home, "config.toml");
    await writeConfigFile(
      path,
      applyConfigPatch({ values: {} }, { url: "http://127.0.0.1:8080", token: "bcn_token" }),
    );
    const source = await readFile(path, "utf8");
    expect(source).toContain('token = "bcn_token"');
    const loaded = await readConfigFile(path);
    expect(loaded.values["token"]).toBe("bcn_token");
  });
});
