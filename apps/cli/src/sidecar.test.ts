import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { startSidecar } from "./sidecar.js";
import { sidecarTunnelUrl } from "./tunnel.js";

describe("sidecar", () => {
  it("binds loopback, writes sidecar.json, and refuses 0.0.0.0", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-sidecar-"));
    await expect(
      startSidecar({
        home,
        cwd: home,
        url: "http://127.0.0.1:8080",
        token: "bcn_" + "A".repeat(43),
        host: "0.0.0.0",
      }),
    ).rejects.toThrow(/127\.0\.0\.1/);

    const started = await startSidecar({
      home,
      cwd: home,
      url: "http://127.0.0.1:8080",
      token: "bcn_" + "A".repeat(43),
      host: "127.0.0.1",
    });
    expect(started.host).toBe("127.0.0.1");
    const saved = JSON.parse(await readFile(join(home, "sidecar.json"), "utf8")) as {
      host: string;
      port: number;
      token: string;
    };
    expect(saved.host).toBe("127.0.0.1");
    expect(saved.port).toBe(started.port);
    expect(saved.token).toBe(started.token);
    await started.close();
  });

  it("registers heartbeats for a project without an outbound tunnel", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-sidecar-"));
    const started = await startSidecar({
      home,
      cwd: home,
      url: "http://127.0.0.1:8080",
      token: "bcn_" + "A".repeat(43),
      projectId: "01934567-89ab-7cde-89ab-0123456789ac",
      host: "127.0.0.1",
      fetchImpl: (async () => Response.json({ items: [] })) as typeof fetch,
    });
    expect(started.host).toBe("127.0.0.1");
    await started.close();
  });
});

describe("sidecarTunnelUrl", () => {
  it("uses BEACON_HOST when set and otherwise derives wss from the control plane", () => {
    expect(sidecarTunnelUrl("https://api.example", "beacon.example")).toBe(
      "wss://beacon.example/v1/sidecar",
    );
    expect(sidecarTunnelUrl("http://127.0.0.1:8080")).toBe("ws://127.0.0.1:8080/v1/sidecar");
  });
});
