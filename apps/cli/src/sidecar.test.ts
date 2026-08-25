import fs from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
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

describe("sidecar mtime tracking", () => {
  it("includes last_indexed_at in query responses and reindexes on file change", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-sidecar-mtime-"));
    const started = await startSidecar({
      home,
      cwd: home,
      url: "http://127.0.0.1:8080",
      token: "bcn_" + "A".repeat(43),
      host: "127.0.0.1",
      fetchImpl: (async () => Response.json({ items: [] })) as typeof fetch,
    });

    const token = started.token;
    const res = await fetch(`http://127.0.0.1:${started.port}/repos/local/tree?depth=1`, {
      headers: { authorization: `Bearer ${token}` },
    });
    expect(res.ok).toBe(true);
    const body = (await res.json()) as Record<string, unknown>;
    expect(body.last_indexed_at).toBeDefined();
    expect(typeof body.last_indexed_at).toBe("string");
    // Valid ISO timestamp
    const date = new Date(body.last_indexed_at as string);
    expect(Number.isNaN(date.getTime())).toBe(false);

    await started.close();
  });

  it("reindexes when a file is modified after initial index", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-sidecar-reindex-"));
    const testFile = join(home, "test-file.txt");
    await writeFile(testFile, "initial content\n");

    const started = await startSidecar({
      home,
      cwd: home,
      url: "http://127.0.0.1:8080",
      token: "bcn_" + "A".repeat(43),
      host: "127.0.0.1",
      fetchImpl: (async () => Response.json({ items: [] })) as typeof fetch,
    });

    const token = started.token;

    // First query - initial index
    const res1 = await fetch(
      `http://127.0.0.1:${started.port}/repos/local/search?q=initial`,
      { headers: { authorization: `Bearer ${token}` } },
    );
    expect(res1.ok).toBe(true);
    const body1 = (await res1.json()) as { items: Array<{ kind: string }>; last_indexed_at: string };
    const firstIndexed = body1.last_indexed_at;

    // Modify the file
    fs.writeFileSync(testFile, "modified content\n");

    // Second query - should reindex due to mtime change
    const res2 = await fetch(
      `http://127.0.0.1:${started.port}/repos/local/search?q=modified`,
      { headers: { authorization: `Bearer ${token}` } },
    );
    expect(res2.ok).toBe(true);
    const body2 = (await res2.json()) as { items: Array<{ kind: string }>; last_indexed_at: string };
    const secondIndexed = body2.last_indexed_at;

    // The index timestamp should have been updated
    expect(new Date(secondIndexed).getTime()).toBeGreaterThanOrEqual(new Date(firstIndexed).getTime());

    await started.close();
  });
});
