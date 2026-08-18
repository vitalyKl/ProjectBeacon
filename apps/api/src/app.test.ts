import { afterEach, describe, expect, it } from "vitest";
import { metrics } from "@beacon/shared";

import { createApp } from "./app.js";
import { SIDECAR_TUNNEL_PATH } from "./code/tunnel.js";

describe("GET /health", () => {
  it("returns 200 without touching the database", async () => {
    const app = createApp({
      checkReady: async () => {
        throw new Error("health must not call ready check");
      },
    });

    const res = await app.request("/health");

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ status: "ok" });
  });
});

describe("GET /metrics", () => {
  afterEach(() => {
    delete process.env.BEACON_METRICS;
    metrics.reset();
  });

  it("is off unless BEACON_METRICS is set", async () => {
    const app = createApp({ checkReady: async () => true });
    const res = await app.request("/metrics");
    expect(res.status).toBe(404);
  });

  it("exposes Prometheus text when enabled", async () => {
    process.env.BEACON_METRICS = "true";
    const app = createApp({ checkReady: async () => true });
    await app.request("/health");
    const res = await app.request("/metrics");
    expect(res.status).toBe(200);
    const body = await res.text();
    expect(body).toContain("beacon_http_requests_total");
    expect(body).toContain('route="/health"');
  });
});

describe("GET /ready", () => {
  it("returns 200 when Postgres is reachable", async () => {
    const app = createApp({ checkReady: async () => true });

    const res = await app.request("/ready");

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ status: "ok" });
  });

  it("returns 503 when Postgres is not reachable", async () => {
    const app = createApp({ checkReady: async () => false });

    const res = await app.request("/ready");

    expect(res.status).toBe(503);
    expect(await res.json()).toEqual({ status: "unavailable" });
  });

  it("returns 503 when the ready check throws", async () => {
    const app = createApp({
      checkReady: async () => {
        throw new Error("connection refused");
      },
    });

    const res = await app.request("/ready");

    expect(res.status).toBe(503);
    expect(await res.json()).toEqual({ status: "unavailable" });
  });
});

describe("GET /v1/sidecar", () => {
  it("does not accept the upgrade when the flag is off", async () => {
    const app = createApp({
      checkReady: async () => true,
      sidecarTunnelEnabled: () => false,
    });
    const res = await app.request(SIDECAR_TUNNEL_PATH, {
      headers: { upgrade: "websocket", connection: "Upgrade" },
    });
    expect(res.status).toBe(404);
  });
});
