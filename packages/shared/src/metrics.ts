export type HistogramSnapshot = {
  count: number;
  sum: number;
  buckets: number[];
};

export type MetricsSnapshot = {
  counters: Record<string, number>;
  gauges: Record<string, number>;
  histograms: Record<string, HistogramSnapshot>;
};

export const HTTP_DURATION_BUCKETS = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10_000];
export const COMPILE_DURATION_BUCKETS = [10, 25, 50, 100, 250, 500, 1000, 2500, 5000];
export const INDEX_DURATION_BUCKETS = [5, 10, 25, 50, 100, 250, 500, 1000, 2500];
export const MCP_DURATION_BUCKETS = [5, 10, 25, 50, 100, 250, 500, 1000, 2500];
export const JOB_DURATION_BUCKETS = [50, 100, 250, 500, 1000, 2500, 5000, 15_000, 60_000];

const LABEL_SEP = "\u001f";

function metricKey(name: string, labels: Record<string, string>): string {
  const parts = Object.keys(labels)
    .sort()
    .map((key) => `${key}=${labels[key] ?? ""}`);
  return parts.length === 0 ? name : `${name}${LABEL_SEP}${parts.join(",")}`;
}

function parseMetricKey(key: string): { name: string; labels: Record<string, string> } {
  const sep = key.indexOf(LABEL_SEP);
  if (sep < 0) {
    return { name: key, labels: {} };
  }
  const name = key.slice(0, sep);
  const labels: Record<string, string> = {};
  for (const part of key.slice(sep + 1).split(",")) {
    const eq = part.indexOf("=");
    if (eq < 0) {
      continue;
    }
    labels[part.slice(0, eq)] = part.slice(eq + 1);
  }
  return { name, labels };
}

function escapeLabel(value: string): string {
  return value.replaceAll("\\", "\\\\").replaceAll("\n", "\\n").replaceAll('"', '\\"');
}

function formatLabels(labels: Record<string, string>): string {
  const keys = Object.keys(labels);
  if (keys.length === 0) {
    return "";
  }
  return `{${keys
    .sort()
    .map((key) => `${key}="${escapeLabel(labels[key] ?? "")}"`)
    .join(",")}}`;
}

class Histogram {
  count = 0;
  sum = 0;
  readonly buckets: number[];

  constructor(private readonly bounds: readonly number[]) {
    this.buckets = Array.from({ length: bounds.length + 1 }, () => 0);
  }

  observe(value: number): void {
    this.count += 1;
    this.sum += value;
    let placed = false;
    for (let i = 0; i < this.bounds.length; i += 1) {
      if (value <= (this.bounds[i] ?? 0)) {
        this.buckets[i] = (this.buckets[i] ?? 0) + 1;
        placed = true;
        break;
      }
    }
    if (!placed) {
      this.buckets[this.buckets.length - 1] = (this.buckets[this.buckets.length - 1] ?? 0) + 1;
    }
  }

  snapshot(): HistogramSnapshot {
    return { count: this.count, sum: this.sum, buckets: [...this.buckets] };
  }
}

export class MetricsRegistry {
  private readonly counters = new Map<string, number>();
  private readonly gauges = new Map<string, number>();
  private readonly histograms = new Map<string, Histogram>();
  private readonly histogramBounds = new Map<string, readonly number[]>();

  inc(name: string, labels: Record<string, string> = {}, delta = 1): void {
    const key = metricKey(name, labels);
    this.counters.set(key, (this.counters.get(key) ?? 0) + delta);
  }

  add(name: string, delta: number, labels: Record<string, string> = {}): void {
    this.inc(name, labels, delta);
  }

  setGauge(name: string, value: number, labels: Record<string, string> = {}): void {
    this.gauges.set(metricKey(name, labels), value);
  }

  observe(
    name: string,
    value: number,
    bounds: readonly number[],
    labels: Record<string, string> = {},
  ): void {
    const key = metricKey(name, labels);
    let histogram = this.histograms.get(key);
    if (!histogram) {
      histogram = new Histogram(bounds);
      this.histograms.set(key, histogram);
      this.histogramBounds.set(name, bounds);
    }
    histogram.observe(value);
  }

  snapshot(): MetricsSnapshot {
    const counters: Record<string, number> = {};
    for (const [key, value] of this.counters) {
      counters[key] = value;
    }
    const gauges: Record<string, number> = {};
    for (const [key, value] of this.gauges) {
      gauges[key] = value;
    }
    const histograms: Record<string, HistogramSnapshot> = {};
    for (const [key, value] of this.histograms) {
      histograms[key] = value.snapshot();
    }
    return { counters, gauges, histograms };
  }

  renderPrometheus(): string {
    const lines: string[] = [];
    const counterGroups = new Map<
      string,
      Array<{ labels: Record<string, string>; value: number }>
    >();
    for (const [key, value] of this.counters) {
      const parsed = parseMetricKey(key);
      const group = counterGroups.get(parsed.name) ?? [];
      group.push({ labels: parsed.labels, value });
      counterGroups.set(parsed.name, group);
    }
    for (const [name, series] of [...counterGroups.entries()].sort(([a], [b]) =>
      a.localeCompare(b),
    )) {
      lines.push(`# TYPE ${name} counter`);
      for (const item of series) {
        lines.push(`${name}${formatLabels(item.labels)} ${item.value}`);
      }
    }

    const gaugeGroups = new Map<string, Array<{ labels: Record<string, string>; value: number }>>();
    for (const [key, value] of this.gauges) {
      const parsed = parseMetricKey(key);
      const group = gaugeGroups.get(parsed.name) ?? [];
      group.push({ labels: parsed.labels, value });
      gaugeGroups.set(parsed.name, group);
    }
    for (const [name, series] of [...gaugeGroups.entries()].sort(([a], [b]) =>
      a.localeCompare(b),
    )) {
      lines.push(`# TYPE ${name} gauge`);
      for (const item of series) {
        lines.push(`${name}${formatLabels(item.labels)} ${item.value}`);
      }
    }

    const histogramGroups = new Map<
      string,
      Array<{
        labels: Record<string, string>;
        snapshot: HistogramSnapshot;
        bounds: readonly number[];
      }>
    >();
    for (const [key, histogram] of this.histograms) {
      const parsed = parseMetricKey(key);
      const bounds = this.histogramBounds.get(parsed.name) ?? [];
      const group = histogramGroups.get(parsed.name) ?? [];
      group.push({ labels: parsed.labels, snapshot: histogram.snapshot(), bounds });
      histogramGroups.set(parsed.name, group);
    }
    for (const [name, series] of [...histogramGroups.entries()].sort(([a], [b]) =>
      a.localeCompare(b),
    )) {
      lines.push(`# TYPE ${name} histogram`);
      for (const item of series) {
        let cumulative = 0;
        for (let i = 0; i < item.bounds.length; i += 1) {
          cumulative += item.snapshot.buckets[i] ?? 0;
          lines.push(
            `${name}_bucket${formatLabels({ ...item.labels, le: String(item.bounds[i]) })} ${cumulative}`,
          );
        }
        cumulative += item.snapshot.buckets[item.snapshot.buckets.length - 1] ?? 0;
        lines.push(`${name}_bucket${formatLabels({ ...item.labels, le: "+Inf" })} ${cumulative}`);
        lines.push(`${name}_sum${formatLabels(item.labels)} ${item.snapshot.sum}`);
        lines.push(`${name}_count${formatLabels(item.labels)} ${item.snapshot.count}`);
      }
    }

    return `${lines.join("\n")}\n`;
  }

  reset(): void {
    this.counters.clear();
    this.gauges.clear();
    this.histograms.clear();
    this.histogramBounds.clear();
  }
}

export const metrics = new MetricsRegistry();

export function observeHttp(
  method: string,
  route: string,
  status: number,
  durationMs: number,
): void {
  const labels = { method: method.toUpperCase(), route, status: String(status) };
  metrics.inc("beacon_http_requests_total", labels);
  metrics.observe("beacon_http_request_duration_ms", durationMs, HTTP_DURATION_BUCKETS, labels);
}

export function observeMcpTool(tool: string, status: string, durationMs: number): void {
  const labels = { tool, status };
  metrics.inc("beacon_mcp_tool_calls_total", labels);
  metrics.observe("beacon_mcp_tool_duration_ms", durationMs, MCP_DURATION_BUCKETS, labels);
}

export function observeCompile(status: string, durationMs: number): void {
  const labels = { status };
  metrics.inc("beacon_compile_total", labels);
  metrics.observe("beacon_compile_duration_ms", durationMs, COMPILE_DURATION_BUCKETS, labels);
}

export function observeIndex(op: string, status: string, durationMs: number): void {
  const labels = { op, status };
  metrics.inc("beacon_index_requests_total", labels);
  metrics.observe("beacon_index_duration_ms", durationMs, INDEX_DURATION_BUCKETS, labels);
}

export function observeJob(queue: string, status: string, durationMs: number): void {
  const labels = { queue, status };
  metrics.inc("beacon_job_total", labels);
  metrics.observe("beacon_job_duration_ms", durationMs, JOB_DURATION_BUCKETS, labels);
}

export function incRateLimited(limit: string): void {
  metrics.inc("beacon_rate_limited_total", { limit });
}

export function addGetFileBytes(bytes: number): void {
  if (bytes > 0) {
    metrics.add("beacon_get_file_bytes", bytes);
  }
}

export function setSidecarConnected(count: number): void {
  metrics.setGauge("beacon_sidecar_connected", count);
}

export function setGithubSyncLagSeconds(seconds: number): void {
  metrics.setGauge("beacon_github_sync_lag_seconds", seconds);
}

export function setApprovalPending(count: number): void {
  metrics.setGauge("beacon_approval_pending", count);
}
