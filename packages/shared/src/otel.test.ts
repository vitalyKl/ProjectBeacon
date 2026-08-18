import { describe, expect, it } from "vitest";

import { isCodeHttpRoute, isCodeToolName, loadOtelConfig, shouldSampleSuccess } from "./otel.js";

describe("otel config", () => {
  it("stays off unless OTEL_TRACES_ENABLED is set", () => {
    expect(loadOtelConfig({})).toMatchObject({ enabled: false });
    expect(loadOtelConfig({ OTEL_EXPORTER_OTLP_ENDPOINT: "http://collector:4318" }).enabled).toBe(
      false,
    );
    expect(loadOtelConfig({ OTEL_TRACES_ENABLED: "true" }).enabled).toBe(true);
  });

  it("samples about 10% of successes", () => {
    expect(shouldSampleSuccess(0.1, 0.09)).toBe(true);
    expect(shouldSampleSuccess(0.1, 0.1)).toBe(false);
  });

  it("force-samples code HTTP routes and MCP code tools only", () => {
    expect(isCodeHttpRoute("/v1/repos/:id/files")).toBe(true);
    expect(isCodeHttpRoute("/v1/repos/:id/tree")).toBe(true);
    expect(isCodeHttpRoute("/v1/repos/:id/symbols")).toBe(true);
    expect(isCodeHttpRoute("/v1/repos/:id/owners")).toBe(true);
    expect(isCodeHttpRoute("/v1/repos/:id/related")).toBe(true);
    expect(isCodeHttpRoute("/v1/tasks/:id/changed-scope")).toBe(true);
    expect(isCodeHttpRoute("/v1/repos/:id/search")).toBe(true);
    expect(isCodeHttpRoute("/v1/projects/:id/context/search")).toBe(false);
    expect(isCodeToolName("get_file")).toBe(true);
    expect(isCodeToolName("get_context_pack")).toBe(false);
  });
});
