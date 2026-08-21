import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { duplicateJsonKeys } from "./json-keys.js";

export type JournalEntry = {
  idx: number;
  tag: string;
};

export function drizzleDir(from: string = import.meta.url): string {
  return join(dirname(fileURLToPath(from)), "..", "drizzle");
}

export function listSqlMigrationFiles(dir: string): string[] {
  return readdirSync(dir)
    .filter((name) => name.endsWith(".sql"))
    .sort();
}

export function parseJournalEntries(parsed: unknown): JournalEntry[] {
  if (typeof parsed !== "object" || parsed === null || !("entries" in parsed)) {
    throw new Error("journal is missing entries");
  }
  const entries = (parsed as { entries: unknown }).entries;
  if (!Array.isArray(entries)) {
    throw new Error("journal entries must be an array");
  }
  return entries.map((entry, index) => {
    if (typeof entry !== "object" || entry === null) {
      throw new Error(`journal entry ${index} is not an object`);
    }
    const record = entry as { idx?: unknown; tag?: unknown };
    if (typeof record.idx !== "number" || typeof record.tag !== "string") {
      throw new Error(`journal entry ${index} is missing idx or tag`);
    }
    return { idx: record.idx, tag: record.tag };
  });
}

export function loadJournal(dir: string = drizzleDir()): {
  source: string;
  entries: JournalEntry[];
  sqlFiles: string[];
} {
  const source = readFileSync(join(dir, "meta", "_journal.json"), "utf8");
  const duplicates = duplicateJsonKeys(source);
  if (duplicates.length > 0) {
    const first = duplicates[0];
    throw new Error(
      `duplicate JSON key "${first?.key}" at ${first?.path} in _journal.json`,
    );
  }
  return {
    source,
    entries: parseJournalEntries(JSON.parse(source) as unknown),
    sqlFiles: listSqlMigrationFiles(dir),
  };
}
