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

  it("dials the control-plane tunnel with the project token", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-sidecar-"));
    const dials: { url: string; headers: Record<string, string> }[] = [];
    const token = "bcn_" + "A".repeat(43);
    const projectId = "01934567-89ab-7cde-89ab-0123456789ac";
    const started = await startSidecar({
      home,
      cwd: home,
      url: "http://127.0.0.1:8080",
      token,
      projectId,
      host: "127.0.0.1",
      fetchImpl: (async () => Response.json({ items: [{ id: projectId }] })) as typeof fetch,
      tunnelDialer: (url, headers) => {
        dials.push({ url, headers });
        return {
          send() {},
          close() {},
          on() {},
        };
      },
    });
    expect(dials).toHaveLength(1);
    expect(dials[0]?.url).toBe("ws://127.0.0.1:8080/v1/sidecar");
    expect(dials[0]?.headers.authorization).toBe(`Bearer ${token}`);
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
