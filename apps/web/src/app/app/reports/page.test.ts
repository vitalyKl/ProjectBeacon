import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

const appRoot = dirname(fileURLToPath(import.meta.url));

function readApp(relativePath: string): string {
  return readFileSync(join(appRoot, relativePath), "utf8");
}

describe("reports page", () => {
  it("imports eval metric helpers and uses useTf for interpolated keys", () => {
    const page = readApp("page.tsx");
    expect(page).toContain('fetchProjectEvalMetrics');
    expect(page).toContain("PublicEvalMetric");
    expect(page).toContain("useTf");
    expect(page).toContain('t("reports.evalMetrics")');
    expect(page).toContain('t("reports.evalMetricsIntro")');
    expect(page).toContain('t("reports.emptyEvalMetrics")');
  });

  it("renders eval metrics column in a 3-column grid", () => {
    const page = readApp("page.tsx");
    expect(page).toContain("lg:grid-cols-3");
    expect(page).toContain("reports.evalMetrics");
    expect(page).toContain("onIngest");
  });

  it("shows eval fixture details when expanded", () => {
    const page = readApp("page.tsx");
    expect(page).toContain("evalSavedTokens");
    expect(page).toContain("evalSavedTurns");
    expect(page).toContain("evalBriefPasses");
    expect(page).toContain("evalWithoutBriefPasses");
    expect(page).toContain("evalBetterPass");
    expect(page).toContain("evalWorsePass");
  });
});
