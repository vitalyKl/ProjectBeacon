import { describe, expect, it } from "vitest";

import { createSidecarTunnelHub } from "./tunnel.js";

describe("sidecar tunnel hub", () => {
  it("routes a request to the live session and samples traffic without bodies", async () => {
    const logs: Record<string, unknown>[] = [];
    const hub = createSidecarTunnelHub({
      now: () => new Date("2026-01-01T00:00:00.010Z"),
      log: (fields) => {
        logs.push(fields);
      },
    });
    hub.register({
      id: "sess-1",
      projectId: "proj",
      tokenId: "tok",
      repoIds: new Set(["repo-1"]),
      request: async (_repoId, path) => ({ status: 200, body: { path, content: "SECRET" } }),
    });
    expect(hub.isLive("repo-1")).toBe(true);
    await expect(hub.query("repo-1", "/files", { path: "src/a.ts" })).resolves.toEqual({
      path: "/files",
      content: "SECRET",
    });
    expect(JSON.stringify(logs)).not.toContain("SECRET");
    expect(logs.some((row) => row["event"] === "request" && row["path"] === "/files")).toBe(true);
  });

  it("returns 503 when no session is live", async () => {
    const hub = createSidecarTunnelHub({ log: () => undefined });
    await expect(hub.query("missing", "/tree")).rejects.toMatchObject({
      status: 503,
      code: "code_index_unavailable",
    });
  });
});
