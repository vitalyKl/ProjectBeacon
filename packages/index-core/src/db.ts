import fs from "node:fs";
import path from "node:path";
import Database from "better-sqlite3";
import { SCHEMA_SQL, SCHEMA_VERSION } from "./schema.js";

export type SqliteDb = Database.Database;

export function openDatabase(dbPath: string): SqliteDb {
  if (dbPath !== ":memory:") {
    fs.mkdirSync(path.dirname(dbPath), { recursive: true });
  }
  const db = new Database(dbPath);
  db.pragma("journal_mode = WAL");
  db.pragma("synchronous = NORMAL");
  db.pragma("foreign_keys = ON");
  migrate(db);
  return db;
}

function migrate(db: SqliteDb): void {
  const hasMeta = db
    .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'meta'")
    .get() as { name: string } | undefined;
  if (!hasMeta) {
    db.exec(SCHEMA_SQL);
    db.prepare("INSERT INTO meta (k, v) VALUES ('schema_version', ?)").run(SCHEMA_VERSION);
    return;
  }
  const row = db.prepare("SELECT v FROM meta WHERE k = 'schema_version'").get() as
    { v: string } | undefined;
  if (!row) {
    db.prepare("INSERT INTO meta (k, v) VALUES ('schema_version', ?)").run(SCHEMA_VERSION);
  }
}

export function resetDatabase(db: SqliteDb): void {
  db.exec(`
    DROP TABLE IF EXISTS files_fts;
    DROP TABLE IF EXISTS symbols_fts;
    DROP TABLE IF EXISTS files;
    DROP TABLE IF EXISTS dirs;
    DROP TABLE IF EXISTS symbols;
    DROP TABLE IF EXISTS imports;
    DROP TABLE IF EXISTS meta;
  `);
  db.exec(SCHEMA_SQL);
  db.prepare("INSERT INTO meta (k, v) VALUES ('schema_version', ?)").run(SCHEMA_VERSION);
}
