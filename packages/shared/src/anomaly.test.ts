import { describe, expect, it } from "vitest";

import { AnomalyTracker, evaluateCodeVolumeAnomaly, GET_FILE_LINE_THRESHOLD } from "./anomaly.js";

describe("anomaly evaluation", () => {
  it("flags code volume above 10x the 7-day baseline", () => {
    expect(evaluateCodeVolumeAnomaly(11, 1)).toEqual({ anomaly: true, threshold: 10 });
    expect(evaluateCodeVolumeAnomaly(10, 1)).toEqual({ anomaly: false, threshold: 10 });
  });

  it("flags get_file lines over 5k in 5 minutes", () => {
    const tracker = new AnomalyTracker();
    const now = new Date("2026-01-01T00:00:00.000Z");
    expect(tracker.recordGetFileLines("tok", 4000, now)).toBeUndefined();
    const hit = tracker.recordGetFileLines("tok", 1500, new Date(now.getTime() + 60_000));
    expect(hit).toMatchObject({
      kind: "get_file_lines",
      token_id: "tok",
      lines: 5500,
      threshold: GET_FILE_LINE_THRESHOLD,
    });
  });

  it("uses prior daily totals as the code-volume baseline", () => {
    const tracker = new AnomalyTracker();
    const day1 = new Date("2026-01-01T12:00:00.000Z");
    tracker.recordCodeCall("tok", day1);
    const day2 = new Date("2026-01-02T00:00:00.000Z");
    tracker.recordCodeCall("tok", day2);
    let last: ReturnType<AnomalyTracker["recordCodeCall"]>;
    for (let i = 0; i < 11; i += 1) {
      last = tracker.recordCodeCall("tok", day2);
    }
    expect(last).toMatchObject({ kind: "code_volume", token_id: "tok" });
  });
});
