import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import {
  createMockDriver,
  formatReport,
  loadFixture,
  runEval,
  runEvalBatch,
  toJsonReport,
  type EvalFixture,
  type RecordedRun,
} from "./eval.js";

function makeFixture(overrides?: {
  taskTitle?: string;
  taskAcceptance?: string;
  withBrief?: Partial<RecordedRun>;
  withoutBrief?: Partial<RecordedRun>;
}): EvalFixture {
  return {
    task: {
      title: overrides?.taskTitle ?? "Update CLI help",
      acceptance: overrides?.taskAcceptance ?? "USAGE must contain eval",
    },
    brief: {
      project_name: "Beacon",
      sections: [
        { id: "conventions", title: "Conventions", body: "Follow the package." },
      ],
    },
    runs: {
      with_brief: {
        tokens_before_edit: overrides?.withBrief?.tokens_before_edit ?? 2000,
        turns: overrides?.withBrief?.turns ?? 5,
        passed: overrides?.withBrief?.passed ?? true,
        brief_source: "compiled" as const,
      },
      without_brief: {
        tokens_before_edit: overrides?.withoutBrief?.tokens_before_edit ?? 5600,
        turns: overrides?.withoutBrief?.turns ?? 10,
        passed: overrides?.withoutBrief?.passed ?? true,
        brief_source: "raw" as const,
      },
    },
  };
}

describe("loadFixture", () => {
  it("loads a valid fixture from disk", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-eval-"));
    await writeFile(
      join(home, "fixture.json"),
      JSON.stringify(makeFixture()),
      "utf-8",
    );
    const fixture = loadFixture(join(home, "fixture.json"));
    expect(fixture.task.title).toBe("Update CLI help");
    expect(fixture.runs.with_brief.tokens_before_edit).toBe(2000);
  });

  it("throws when task.title is missing", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-eval-"));
    const bad = { ...makeFixture(), task: { acceptance: "must pass" } };
    await writeFile(join(home, "bad.json"), JSON.stringify(bad), "utf-8");
    expect(() => loadFixture(join(home, "bad.json"))).toThrow("missing task.title");
  });

  it("throws when task.acceptance is missing", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-eval-"));
    const bad = { ...makeFixture(), task: { title: "something" } };
    await writeFile(join(home, "bad.json"), JSON.stringify(bad), "utf-8");
    expect(() => loadFixture(join(home, "bad.json"))).toThrow("missing task.acceptance");
  });

  it("throws when runs.with_brief is missing", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-eval-"));
    const bad = { ...makeFixture(), runs: { without_brief: makeFixture().runs.without_brief } };
    await writeFile(join(home, "bad.json"), JSON.stringify(bad), "utf-8");
    expect(() => loadFixture(join(home, "bad.json"))).toThrow("missing runs.with_brief");
  });

  it("throws when runs.without_brief is missing", async () => {
    const home = await mkdtemp(join(tmpdir(), "beacon-eval-"));
    const bad = { ...makeFixture(), runs: { with_brief: makeFixture().runs.with_brief } };
    await writeFile(join(home, "bad.json"), JSON.stringify(bad), "utf-8");
    expect(() => loadFixture(join(home, "bad.json"))).toThrow("missing runs.without_brief");
  });
});

describe("createMockDriver", () => {
  it("returns recorded values for with_brief", () => {
    const fixture = makeFixture({
      withBrief: { tokens_before_edit: 42, turns: 7, passed: true },
    });
    const driver = createMockDriver();
    const result = driver.run(fixture, "with_brief");
    expect(result.tokens_before_edit).toBe(42);
    expect(result.turns).toBe(7);
    expect(result.passed).toBe(true);
    expect(result.brief_source).toBe("compiled");
  });

  it("returns recorded values for without_brief", () => {
    const fixture = makeFixture({
      withoutBrief: { tokens_before_edit: 100, turns: 3, passed: false },
    });
    const driver = createMockDriver();
    const result = driver.run(fixture, "without_brief");
    expect(result.tokens_before_edit).toBe(100);
    expect(result.turns).toBe(3);
    expect(result.passed).toBe(false);
    expect(result.brief_source).toBe("raw");
  });
});

describe("runEval", () => {
  it("produces a report with schema_version 1", () => {
    const report = runEval(makeFixture(), createMockDriver());
    expect(report.schema_version).toBe("1");
    expect(typeof report.generated_at).toBe("string");
  });

  it("computes saved_tokens and saved_turns correctly", () => {
    const report = runEval(makeFixture(), createMockDriver());
    const [f] = report.fixtures;
    expect(f!.savings.saved_tokens).toBe(3600);
    expect(f!.savings.saved_turns).toBe(5);
  });

  it("detects better_pass when only with_brief passes", () => {
    const fixture = makeFixture({
      withBrief: { passed: true },
      withoutBrief: { passed: false },
    });
    const report = runEval(fixture, createMockDriver());
    expect(report.fixtures[0]!.savings.better_pass).toBe(true);
    expect(report.fixtures[0]!.savings.worse_pass).toBe(false);
  });

  it("detects worse_pass when only without_brief passes", () => {
    const fixture = makeFixture({
      withBrief: { passed: false },
      withoutBrief: { passed: true },
    });
    const report = runEval(fixture, createMockDriver());
    expect(report.fixtures[0]!.savings.better_pass).toBe(false);
    expect(report.fixtures[0]!.savings.worse_pass).toBe(true);
  });

  it("totals reflect both runs passing", () => {
    const report = runEval(makeFixture(), createMockDriver());
    expect(report.totals.with_brief_passes).toBe(1);
    expect(report.totals.without_brief_passes).toBe(1);
  });

  it("totals reflect only with_brief passing", () => {
    const fixture = makeFixture({
      withBrief: { passed: true },
      withoutBrief: { passed: false },
    });
    const report = runEval(fixture, createMockDriver());
    expect(report.totals.with_brief_passes).toBe(1);
    expect(report.totals.without_brief_passes).toBe(0);
  });

  it("with_brief_avg_tokens reflects the recorded value", () => {
    const report = runEval(makeFixture(), createMockDriver());
    expect(report.totals.with_brief_avg_tokens).toBe(2000);
    expect(report.totals.with_brief_avg_turns).toBe(5);
  });
});

describe("runEvalBatch", () => {
  it("aggregates savings across multiple fixtures", () => {
    const f1 = makeFixture({ withBrief: { tokens_before_edit: 100 } });
    const f2 = makeFixture({ withBrief: { tokens_before_edit: 200 } });
    const report = runEvalBatch([f1, f2], createMockDriver());
    expect(report.fixtures.length).toBe(2);
    expect(report.totals.total_saved_tokens).toBe(10900);
    expect(report.totals.with_brief_avg_tokens).toBeCloseTo(150);
  });

  it("divides by fixture count for averages", () => {
    const report = runEvalBatch(
      [makeFixture({ withBrief: { turns: 4 } }), makeFixture({ withBrief: { turns: 6 } })],
      createMockDriver(),
    );
    expect(report.totals.with_brief_avg_turns).toBeCloseTo(5);
  });
});

describe("formatReport", () => {
  it("includes the header and schema version", () => {
    const report = runEval(makeFixture(), createMockDriver());
    const text = formatReport(report);
    expect(text).toContain("Context-Effectiveness Eval Report");
    expect(text).toContain("Schema: v1");
  });

  it("includes per-fixture details", () => {
    const report = runEval(makeFixture(), createMockDriver());
    const text = formatReport(report);
    expect(text).toContain("tokens_before_edit: 2000");
    expect(text).toContain("turns: 5");
    expect(text).toContain("passed: true");
    expect(text).toContain("saved_tokens: 3600");
    expect(text).toContain("saved_turns: 5");
  });

  it("includes totals section", () => {
    const report = runEval(makeFixture(), createMockDriver());
    const text = formatReport(report);
    expect(text).toContain("=== Totals ===");
    expect(text).toContain("total_saved_tokens: 3600");
  });

  it("marks better_pass in the output", () => {
    const fixture = makeFixture({
      withBrief: { passed: true },
      withoutBrief: { passed: false },
    });
    const report = runEval(fixture, createMockDriver());
    const text = formatReport(report);
    expect(text).toContain("Brief improved pass/fail");
  });
});

describe("toJsonReport", () => {
  it("produces valid JSON", () => {
    const report = runEval(makeFixture(), createMockDriver());
    const json = toJsonReport(report);
    const parsed = JSON.parse(json);
    expect(parsed.schema_version).toBe("1");
    expect(parsed.fixtures.length).toBe(1);
  });
});
