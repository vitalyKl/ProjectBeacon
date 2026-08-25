import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** A single recorded run inside a fixture (with or without brief). */
export type RecordedRun = {
  /** Tokens consumed by the agent before making the first correct edit. */
  tokens_before_edit: number;
  /** Wall-clock turns (agent steps) until completion. */
  turns: number;
  /** Whether the acceptance criterion was met. */
  passed: boolean;
  /** How the brief was provided to the agent. */
  brief_source: "compiled" | "raw";
};

/** A single eval fixture defining one task + two runs. */
export type EvalFixture = {
  /** Free-text task definition (title + acceptance criterion). */
  task: {
    title: string;
    acceptance: string;
  };
  /** A compiled session brief to pass with the "with_brief" run. */
  brief?: {
    project_name: string;
    sections: { id: string; title: string; body: string }[];
  };
  /** Two recorded runs: one with brief, one without. */
  runs: {
    with_brief: RecordedRun;
    without_brief: RecordedRun;
  };
};

/** A complete eval report (seed shape for A3 Context Cost report). */
export type EvalReport = {
  schema_version: string;
  generated_at: string;
  fixtures: Array<{
    task: { title: string; acceptance: string };
    brief_provided: boolean;
    with_brief: RecordedRun;
    without_brief: RecordedRun;
    savings: {
      saved_tokens: number;
      saved_turns: number;
      better_pass: boolean;
      worse_pass: boolean;
    };
  }>;
  totals: {
    total_saved_tokens: number;
    total_saved_turns: number;
    with_brief_passes: number;
    without_brief_passes: number;
    with_brief_avg_turns: number;
    with_brief_avg_tokens: number;
  };
};

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

export const EVAL_HELP = `Usage: beacon eval <fixture-path>

Evaluate context-effectiveness by running a fixture twice: with a compiled
session brief and with raw repo access only (no brief). Metrics recorded:

  tokens_before_edit   Tokens consumed before the first correct edit.
  turns                Wall-clock agent turns to completion.
  pass/fail            Whether the acceptance criterion was met.

Each fixture compares the two runs and reports saved_tokens and saved_turns.
The report JSON (schema_version 1) is the seed shape that A3 ingests for
the Context Cost report.

Options:
  --help    Show this help
`;

/** The three metrics printed by --help. */
export const EVAL_METRICS = [
  "tokens_before_edit",
  "turns",
  "pass/fail",
] as const;

// ---------------------------------------------------------------------------
// Fixture loading
// ---------------------------------------------------------------------------

/** Load and validate an eval fixture from a JSON file. */
export function loadFixture(filePath: string): EvalFixture {
  const resolved = resolve(filePath);
  const raw = readFileSync(resolved, "utf-8");
  const parsed = JSON.parse(raw);

  // Minimal validation: require task, runs.with_brief, runs.without_brief.
  if (!parsed.task?.title) {
    throw new Error(`Invalid fixture: missing task.title in ${resolved}`);
  }
  if (!parsed.task?.acceptance) {
    throw new Error(`Invalid fixture: missing task.acceptance in ${resolved}`);
  }
  if (!parsed.runs?.with_brief) {
    throw new Error(`Invalid fixture: missing runs.with_brief in ${resolved}`);
  }
  if (!parsed.runs?.without_brief) {
    throw new Error(`Invalid fixture: missing runs.without_brief in ${resolved}`);
  }
  for (const key of ["tokens_before_edit", "turns"] as const) {
    if (typeof parsed.runs.with_brief[key] !== "number") {
      throw new Error(`Invalid fixture: runs.with_brief.${key} must be a number`);
    }
  }

  return parsed as EvalFixture;
}

// ---------------------------------------------------------------------------
// Mock driver
// ---------------------------------------------------------------------------

/**
 * Replay a fixture using recorded outcomes. This driver does NOT call a real
 * model — it simply returns the pre-recorded metrics from the fixture.
 */
export function createMockDriver() {
  return {
    run(fixture: EvalFixture, mode: "with_brief" | "without_brief"): RecordedRun {
      return fixture.runs[mode];
    },
  };
}

// ---------------------------------------------------------------------------
// Report generation
// ---------------------------------------------------------------------------

/**
 * Run all fixture runs through the driver and produce an EvalReport.
 */
export function runEval(
  fixture: EvalFixture,
  driver: ReturnType<typeof createMockDriver>,
): EvalReport {
  const withBrief = driver.run(fixture, "with_brief");
  const withoutBrief = driver.run(fixture, "without_brief");

  const savedTokens = withoutBrief.tokens_before_edit - withBrief.tokens_before_edit;
  const savedTurns = withoutBrief.turns - withBrief.turns;
  const betterPass = withBrief.passed && !withoutBrief.passed;
  const worsePass = !withBrief.passed && withoutBrief.passed;

  const report: EvalReport = {
    schema_version: "1",
    generated_at: new Date().toISOString(),
    fixtures: [
      {
        task: fixture.task,
        brief_provided: Boolean(fixture.brief),
        with_brief: withBrief,
        without_brief: withoutBrief,
        savings: {
          saved_tokens: savedTokens,
          saved_turns: savedTurns,
          better_pass: betterPass,
          worse_pass: worsePass,
        },
      },
    ],
    totals: {
      total_saved_tokens: savedTokens,
      total_saved_turns: savedTurns,
      with_brief_passes: withBrief.passed ? 1 : 0,
      without_brief_passes: withoutBrief.passed ? 1 : 0,
      with_brief_avg_turns: withBrief.turns,
      with_brief_avg_tokens: withBrief.tokens_before_edit,
    },
  };

  return report;
}

/**
 * Run eval across multiple fixtures.
 */
export function runEvalBatch(
  fixtures: EvalFixture[],
  driver: ReturnType<typeof createMockDriver>,
): EvalReport {
  const allFixtures: EvalReport["fixtures"] = [];
  let totalSavedTokens = 0;
  let totalSavedTurns = 0;
  let with_brief_passes = 0;
  let without_brief_passes = 0;
  let totalWithBriefTurns = 0;
  let totalWithBriefTokens = 0;

  for (const fixture of fixtures) {
    const report = runEval(fixture, driver);
    allFixtures.push(...report.fixtures);
    totalSavedTokens += report.totals.total_saved_tokens;
    totalSavedTurns += report.totals.total_saved_turns;
    with_brief_passes += report.totals.with_brief_passes;
    without_brief_passes += report.totals.without_brief_passes;
    totalWithBriefTurns += report.totals.with_brief_avg_turns;
    totalWithBriefTokens += report.totals.with_brief_avg_tokens;
  }

  return {
    schema_version: "1",
    generated_at: new Date().toISOString(),
    fixtures: allFixtures,
    totals: {
      total_saved_tokens: totalSavedTokens,
      total_saved_turns: totalSavedTurns,
      with_brief_passes,
      without_brief_passes,
      with_brief_avg_turns: fixtures.length > 0 ? totalWithBriefTurns / fixtures.length : 0,
      with_brief_avg_tokens: fixtures.length > 0 ? totalWithBriefTokens / fixtures.length : 0,
    },
  };
}

// ---------------------------------------------------------------------------
// Pretty-print report
// ---------------------------------------------------------------------------

/**
 * Format the eval report as human-readable text.
 */
export function formatReport(report: EvalReport): string {
  const lines: string[] = [];

  lines.push("=== Context-Effectiveness Eval Report ===");
  lines.push(`Schema: v${report.schema_version}`);
  lines.push(`Generated: ${report.generated_at}`);
  lines.push(`Fixtures: ${report.fixtures.length}`);
  lines.push("");

  for (const f of report.fixtures) {
    lines.push(`--- ${f.task.title} ---`);
    lines.push(`Acceptance: ${f.task.acceptance}`);
    lines.push(`Brief provided: ${f.brief_provided}`);
    lines.push("");
    lines.push("  With brief:");
    lines.push(`    tokens_before_edit: ${f.with_brief.tokens_before_edit}`);
    lines.push(`    turns: ${f.with_brief.turns}`);
    lines.push(`    passed: ${f.with_brief.passed}`);
    lines.push("");
    lines.push("  Without brief:");
    lines.push(`    tokens_before_edit: ${f.without_brief.tokens_before_edit}`);
    lines.push(`    turns: ${f.without_brief.turns}`);
    lines.push(`    passed: ${f.without_brief.passed}`);
    lines.push("");
    lines.push(`  Savings:`);
    lines.push(`    saved_tokens: ${f.savings.saved_tokens}`);
    lines.push(`    saved_turns: ${f.savings.saved_turns}`);
    if (f.savings.better_pass) {
      lines.push(`    * Brief improved pass/fail`);
    }
    if (f.savings.worse_pass) {
      lines.push(`    * Brief caused regression`);
    }
    lines.push("");
  }

  lines.push("=== Totals ===");
  lines.push(`total_saved_tokens: ${report.totals.total_saved_tokens}`);
  lines.push(`total_saved_turns: ${report.totals.total_saved_turns}`);
  lines.push(`with_brief_passes: ${report.totals.with_brief_passes}/${report.fixtures.length}`);
  lines.push(`without_brief_passes: ${report.totals.without_brief_passes}/${report.fixtures.length}`);
  lines.push(`with_brief_avg_turns: ${report.totals.with_brief_avg_turns}`);
  lines.push(`with_brief_avg_tokens: ${report.totals.with_brief_avg_tokens}`);

  return lines.join("\n");
}

// ---------------------------------------------------------------------------
// JSON report
// ---------------------------------------------------------------------------

/** Serialize an eval report to JSON string. */
export function toJsonReport(report: EvalReport): string {
  return JSON.stringify(report, null, 2);
}

// ---------------------------------------------------------------------------
// CLI handler
// ---------------------------------------------------------------------------

export type EvalCliOptions = {
  filePath: string;
  format: "json" | "text";
};

/**
 * Handle the `beacon eval` CLI command.
 *
 * @returns 0 on success, 1 on error.
 */
export async function runEvalCommand(
  options: EvalCliOptions,
  io: { stdout: { write(s: string): void }; stderr: { write(s: string): void } },
): Promise<number> {
  try {
    const fixture = loadFixture(options.filePath);
    const driver = createMockDriver();
    const report = runEval(fixture, driver);
    if (options.format === "json") {
      io.stdout.write(toJsonReport(report) + "\n");
    } else {
      io.stdout.write(formatReport(report) + "\n");
    }
    return 0;
  } catch (err) {
    const msg = err instanceof Error ? err.message : "unexpected error";
    io.stderr.write(`eval: ${msg}\n`);
    return 1;
  }
}
