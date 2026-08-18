import { describe, expect, it } from "vitest";

import { loadOtelConfig, shouldSampleSuccess } from "./otel.js";

describe("otel config", () => {
  it("stays off unless an exporter endpoint or explicit flag is set", () => {
    expect(loadOtelConfig({})).toMatchObject({ enabled: false, endpoint: undefined });
    expect(loadOtelConfig({ OTEL_EXPORTER_OTLP_ENDPOINT: "http://collector:4318" })).toMatchObject({
      enabled: true,
      endpoint: "http://collector:4318",
    });
    expect(loadOtelConfig({ OTEL_TRACES_ENABLED: "true" }).enabled).toBe(true);
  });

  it("samples about 10% of successes", () => {
    expect(shouldSampleSuccess(0.1, 0.09)).toBe(true);
    expect(shouldSampleSuccess(0.1, 0.1)).toBe(false);
  });
});
