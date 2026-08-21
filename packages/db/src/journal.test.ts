import { readFileSync } from "node:fs";
import { join } from "node:path";

import { describe, expect, it } from "vitest";

import { drizzleDir, loadJournal } from "./journal.js";

describe("drizzle journal", () => {
  it("has one journal entry per SQL file, in idx order, with matching tags", () => {
    const dir = drizzleDir();
    const { entries, sqlFiles } = loadJournal(dir);
    expect(sqlFiles.length).toBeGreaterThan(0);
    expect(entries.map((entry) => `${entry.tag}.sql`).sort()).toEqual([...sqlFiles].sort());
    expect(new Set(entries.map((entry) => entry.tag)).size).toBe(sqlFiles.length);
    expect(entries.map((entry) => entry.idx)).toEqual(sqlFiles.map((_, index) => index));
  });

  it("keeps the GitHub-issue unique index visible to migrate, including an idempotent follow-up", () => {
    const dir = drizzleDir();
    const { entries } = loadJournal(dir);
    const tags = new Set(entries.map((entry) => entry.tag));
    expect(tags.has("0001_tasks_github_issue_unique")).toBe(true);
    expect(tags.has("0005_tasks_github_issue_unique_if_not_exists")).toBe(true);

    const original = readFileSync(join(dir, "0001_tasks_github_issue_unique.sql"), "utf8");
    const followUp = readFileSync(
      join(dir, "0005_tasks_github_issue_unique_if_not_exists.sql"),
      "utf8",
    );
    expect(original).toContain('CREATE UNIQUE INDEX "tasks_project_github_issue_id_unique"');
    expect(followUp).toContain(
      'CREATE UNIQUE INDEX IF NOT EXISTS "tasks_project_github_issue_id_unique"',
    );
  });
});
