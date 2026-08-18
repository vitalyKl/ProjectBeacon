export const CODE_VOLUME_MULTIPLIER = 10;
export const GET_FILE_LINE_WINDOW_MS = 5 * 60_000;
export const GET_FILE_LINE_THRESHOLD = 5000;

export type CodeVolumeSample = {
  tokenId: string;
  count: number;
  windowStartMs: number;
};

export type GetFileLineSample = {
  tokenId: string;
  lines: number;
  atMs: number;
};

export type Anomaly =
  | {
      kind: "code_volume";
      token_id: string;
      count: number;
      baseline: number;
      threshold: number;
    }
  | {
      kind: "get_file_lines";
      token_id: string;
      lines: number;
      window_ms: number;
      threshold: number;
    };

export function evaluateCodeVolumeAnomaly(
  current: number,
  baseline: number,
  multiplier = CODE_VOLUME_MULTIPLIER,
): { anomaly: true; threshold: number } | { anomaly: false; threshold: number } {
  const threshold = Math.max(1, baseline) * multiplier;
  if (current > threshold) {
    return { anomaly: true, threshold };
  }
  return { anomaly: false, threshold };
}

export function evaluateGetFileLineAnomaly(
  lines: number,
  threshold = GET_FILE_LINE_THRESHOLD,
): boolean {
  return lines > threshold;
}

export class AnomalyTracker {
  private readonly daily = new Map<string, { day: string; count: number }>();
  private readonly history = new Map<string, number[]>();
  private readonly fileLines: GetFileLineSample[] = [];

  recordCodeCall(tokenId: string, at = new Date()): Anomaly | undefined {
    const day = at.toISOString().slice(0, 10);
    const current = this.daily.get(tokenId);
    if (!current || current.day !== day) {
      if (current) {
        const hist = this.history.get(tokenId) ?? [];
        hist.push(current.count);
        this.history.set(tokenId, hist.slice(-7));
      }
      this.daily.set(tokenId, { day, count: 1 });
      return undefined;
    }
    current.count += 1;
    const hist = this.history.get(tokenId) ?? [];
    if (hist.length === 0) {
      return undefined;
    }
    const baseline = hist.reduce((sum, value) => sum + value, 0) / hist.length;
    const check = evaluateCodeVolumeAnomaly(current.count, baseline);
    if (!check.anomaly) {
      return undefined;
    }
    return {
      kind: "code_volume",
      token_id: tokenId,
      count: current.count,
      baseline,
      threshold: check.threshold,
    };
  }

  recordGetFileLines(tokenId: string, lines: number, at = new Date()): Anomaly | undefined {
    if (lines <= 0) {
      return undefined;
    }
    const atMs = at.getTime();
    this.fileLines.push({ tokenId, lines, atMs });
    const cutoff = atMs - GET_FILE_LINE_WINDOW_MS;
    let windowLines = 0;
    let write = 0;
    for (const sample of this.fileLines) {
      if (sample.atMs < cutoff) {
        continue;
      }
      this.fileLines[write] = sample;
      write += 1;
      if (sample.tokenId === tokenId) {
        windowLines += sample.lines;
      }
    }
    this.fileLines.length = write;
    if (!evaluateGetFileLineAnomaly(windowLines)) {
      return undefined;
    }
    return {
      kind: "get_file_lines",
      token_id: tokenId,
      lines: windowLines,
      window_ms: GET_FILE_LINE_WINDOW_MS,
      threshold: GET_FILE_LINE_THRESHOLD,
    };
  }
}

export const anomalyTracker = new AnomalyTracker();
