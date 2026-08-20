import {
  anomalyTracker,
  endSpan,
  isCodeHttpRoute,
  loadOtelConfig,
  metrics,
  newTraceId,
  observeHttp,
  parseBoolEnv,
  setApprovalPending,
  setGithubSyncLagSeconds,
  setSidecarConnected,
  startSpan,
  writeLog,
  type Anomaly,
  type OtelConfig,
  type Span,
} from "@beacon/shared";
import type { Context, Hono } from "hono";

import { SIDECAR_SEEN_MS } from "./code/gateway.js";
import type { JobStore } from "./jobs/store.js";

export function metricsEnabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return parseBoolEnv(env["BEACON_METRICS"]);
}

export function requestTraceId(c: Context): string {
  const incoming = c.req.header("traceparent")?.trim();
  if (incoming) {
    const parts = incoming.split("-");
    if (parts[1] && /^[0-9a-f]{32}$/i.test(parts[1])) {
      return parts[1];
    }
  }
  const existing = c.req.header("x-trace-id")?.trim();
  if (existing) {
    return existing;
  }
  return newTraceId();
}

export function logAnomaly(anomaly: Anomaly, extra: Record<string, unknown> = {}): void {
  writeLog({
    level: "warn",
    msg: "anomaly",
    ...anomaly,
    ...extra,
  });
}

export function recordCodeAnomaly(tokenId: string, at?: Date): void {
  const hit = anomalyTracker.recordCodeCall(tokenId, at);
  if (hit) {
    logAnomaly(hit);
  }
}

export function recordGetFileAnomaly(tokenId: string, lines: number, at?: Date): void {
  const hit = anomalyTracker.recordGetFileLines(tokenId, lines, at);
  if (hit) {
    logAnomaly(hit);
  }
}

export function fileLineCount(result: unknown): number {
  if (result === null || typeof result !== "object" || Array.isArray(result)) {
    return 0;
  }
  const record = result as { start_line?: unknown; end_line?: unknown; content?: unknown };
  if (typeof record.start_line === "number" && typeof record.end_line === "number") {
    return Math.max(0, record.end_line - record.start_line + 1);
  }
  if (typeof record.content === "string") {
    return record.content.length === 0 ? 0 : record.content.split("\n").length;
  }
  return 0;
}

function routeTemplate(path: string): string {
  if (path === "/metrics") {
    return "/metrics";
  }
  return path.replace(
    /[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/gi,
    ":id",
  );
}

function errorCodeOf(body: unknown): string | undefined {
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return undefined;
  }
  const code = (body as { error?: { code?: unknown } }).error?.code;
  return typeof code === "string" ? code : undefined;
}

export function mountObservability(app: Hono): void {
  setSidecarConnected(0);
  setGithubSyncLagSeconds(0);
  setApprovalPending(0);
  app.use("*", async (c, next) => {
    if (c.req.path === "/metrics") {
      await next();
      return;
    }
    const started = Date.now();
    const traceId = requestTraceId(c);
    c.header("x-trace-id", traceId);
    const span = startHttpSpan(c, loadOtelConfig(process.env, "beacon-api"), traceId);
    let failed = false;
    try {
      await next();
    } catch (error) {
      failed = true;
      throw error;
    } finally {
      const durationMs = Date.now() - started;
      const route = routeTemplate(c.req.path);
      const status = failed ? 500 : c.res.status;
      observeHttp(c.req.method, route, status, durationMs);
      let errorCode: string | undefined;
      if (!failed && status >= 400) {
        try {
          errorCode = errorCodeOf(await c.res.clone().json());
        } catch {
          errorCode = undefined;
        }
      }
      if (failed || status >= 500) {
        writeLog({
          level: "error",
          msg: "http",
          trace_id: traceId,
          route,
          duration_ms: durationMs,
          status,
          method: c.req.method,
          ...(errorCode ? { error: { code: errorCode } } : {}),
        });
      }
      endSpan(span, failed || status >= 400 ? "error" : "ok", errorCode);
    }
  });

  app.get("/metrics", (c) => {
    if (!metricsEnabled()) {
      return c.json({ status: "disabled" }, 404);
    }
    return c.body(metrics.renderPrometheus(), 200, {
      "content-type": "text/plain; version=0.0.4; charset=utf-8",
    });
  });
}

function startHttpSpan(c: Context, config: OtelConfig, traceId: string): Span {
  const route = routeTemplate(c.req.path);
  return startSpan("http.request", {
    config,
    traceId,
    forceSample: isCodeHttpRoute(route),
    attributes: { route, method: c.req.method },
  });
}

export async function refreshObservabilityGauges(
  store: Pick<
    JobStore,
    "listSidecarConnections" | "countPendingApprovals" | "githubSyncLagSeconds"
  >,
  now = new Date(),
): Promise<void> {
  const sidecars = await store.listSidecarConnections();
  const connected = sidecars.filter(
    (row) => now.getTime() - row.lastSeenAt.getTime() <= SIDECAR_SEEN_MS,
  ).length;
  setSidecarConnected(connected);
  setApprovalPending(await store.countPendingApprovals());
  setGithubSyncLagSeconds(await store.githubSyncLagSeconds(now));
}
