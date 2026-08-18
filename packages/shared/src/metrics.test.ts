import { describe, expect, it } from "vitest";

import { MetricsRegistry } from "./metrics.js";

describe("MetricsRegistry", () => {
  it("renders named beacon series", () => {
    const registry = new MetricsRegistry();
    registry.inc("beacon_http_requests_total", { method: "GET", route: "/health", status: "200" });
    registry.inc("beacon_rate_limited_total", { limit: "code" });
    registry.add("beacon_get_file_bytes", 128);
    registry.setGauge("beacon_sidecar_connected", 2);
    registry.setGauge("beacon_github_sync_lag_seconds", 0);
    registry.setGauge("beacon_approval_pending", 0);
    registry.observe("beacon_compile_duration_ms", 12, [10, 50, 100], { status: "ok" });

    const text = registry.renderPrometheus();
    expect(text).toContain("beacon_http_requests_total");
    expect(text).toContain("beacon_rate_limited_total");
    expect(text).toContain("beacon_get_file_bytes");
    expect(text).toContain("beacon_sidecar_connected");
    expect(text).toContain("beacon_github_sync_lag_seconds 0");
    expect(text).toContain("beacon_approval_pending 0");
    expect(text).toContain("beacon_compile_duration_ms_count");
  });
});
