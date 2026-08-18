import { randomBytes } from "node:crypto";

export type SpanStatus = "ok" | "error";

export type Span = {
  name: string;
  traceId: string;
  spanId: string;
  startMs: number;
  attributes: Record<string, string | number | boolean>;
};

export type OtelConfig = {
  enabled: boolean;
  serviceName: string;
  sampleRatio: number;
};

const HEX = "0123456789abcdef";

function randomHex(bytes: number): string {
  const buf = randomBytes(bytes);
  let out = "";
  for (const value of buf) {
    out += HEX[(value >> 4) & 0xf];
    out += HEX[value & 0xf];
  }
  return out;
}

export function parseBoolEnv(value: string | undefined): boolean {
  if (!value) {
    return false;
  }
  const normalized = value.trim().toLowerCase();
  return normalized === "1" || normalized === "true" || normalized === "yes" || normalized === "on";
}

export function loadOtelConfig(
  env: NodeJS.ProcessEnv = process.env,
  serviceName = "beacon",
): OtelConfig {
  const enabled = parseBoolEnv(env["OTEL_TRACES_ENABLED"]);
  const ratioRaw = env["OTEL_TRACES_SAMPLER_ARG"]?.trim();
  const parsed = ratioRaw ? Number.parseFloat(ratioRaw) : 0.1;
  const sampleRatio = Number.isFinite(parsed) ? Math.min(1, Math.max(0, parsed)) : 0.1;
  return {
    enabled,
    serviceName: env["OTEL_SERVICE_NAME"]?.trim() || serviceName,
    sampleRatio,
  };
}

export function newTraceId(): string {
  return randomHex(16);
}

export function newSpanId(): string {
  return randomHex(8);
}

export function shouldSampleSuccess(sampleRatio: number, rand = Math.random()): boolean {
  return rand < sampleRatio;
}

const CODE_HTTP_SUFFIXES = [
  "/tree",
  "/files",
  "/symbols",
  "/owners",
  "/related",
  "/changed-scope",
] as const;

const CODE_HTTP_SEARCH = /\/repos\/[^/]+\/search(?:\?|$)/;
const CODE_TOOLS = new Set([
  "get_tree",
  "search_code",
  "get_file",
  "get_symbol",
  "get_owners",
  "get_related_files",
  "get_changed_scope",
]);

export function isCodeHttpRoute(route: string): boolean {
  const path = route.split("?")[0] ?? route;
  if (CODE_HTTP_SEARCH.test(path)) {
    return true;
  }
  return CODE_HTTP_SUFFIXES.some((suffix) => path.endsWith(suffix));
}

export function isCodeToolName(tool: string): boolean {
  return CODE_TOOLS.has(tool);
}

type ActiveSpan = Span & { sampled: boolean; enabled: boolean };

const active = new Map<string, ActiveSpan>();

function exportSpan(span: ActiveSpan, status: SpanStatus, errorCode?: string): void {
  if (!span.sampled) {
    return;
  }
  const durationMs = Math.max(0, Date.now() - span.startMs);
  const payload: Record<string, unknown> = {
    level: status === "error" ? "error" : "debug",
    msg: "span",
    trace_id: span.traceId,
    span: span.name,
    duration_ms: durationMs,
    status,
    service: span.attributes["service.name"],
  };
  if (errorCode) {
    payload["error"] = { code: errorCode };
  }
  for (const [key, value] of Object.entries(span.attributes)) {
    if (key === "service.name") {
      continue;
    }
    payload[key] = value;
  }
  process.stderr.write(`${JSON.stringify(payload)}\n`);
}

export function startSpan(
  name: string,
  options: {
    config?: OtelConfig;
    traceId?: string;
    attributes?: Record<string, string | number | boolean>;
    forceSample?: boolean;
    error?: boolean;
  } = {},
): Span {
  const config = options.config ?? loadOtelConfig();
  const sampled =
    config.enabled &&
    (options.forceSample || options.error || shouldSampleSuccess(config.sampleRatio));
  const span: ActiveSpan = {
    name,
    traceId: options.traceId ?? newTraceId(),
    spanId: newSpanId(),
    startMs: Date.now(),
    attributes: {
      "service.name": config.serviceName,
      ...(options.attributes ?? {}),
    },
    sampled,
    enabled: config.enabled,
  };
  active.set(span.spanId, span);
  return span;
}

export function endSpan(span: Span, status: SpanStatus = "ok", errorCode?: string): void {
  const live = active.get(span.spanId);
  if (!live) {
    return;
  }
  active.delete(span.spanId);
  if (status === "error" && live.enabled) {
    live.sampled = true;
  }
  exportSpan(live, status, errorCode);
}

export function withSpan<T>(
  name: string,
  options: Parameters<typeof startSpan>[1],
  fn: (span: Span) => Promise<T> | T,
): Promise<T> | T {
  const span = startSpan(name, options);
  try {
    const result = fn(span);
    if (result && typeof result === "object" && "then" in result) {
      return Promise.resolve(result).then(
        (value) => {
          endSpan(span, "ok");
          return value;
        },
        (error: unknown) => {
          endSpan(span, "error");
          throw error;
        },
      );
    }
    endSpan(span, "ok");
    return result;
  } catch (error) {
    endSpan(span, "error");
    throw error;
  }
}
