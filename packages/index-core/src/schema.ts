export const SCHEMA_SQL = `
CREATE TABLE files (
  path TEXT PRIMARY KEY,
  lang TEXT,
  size INTEGER,
  mtime INTEGER,
  sha256 TEXT,
  blob_sha TEXT,
  is_binary INTEGER NOT NULL DEFAULT 0,
  indexed_at INTEGER
);
CREATE TABLE dirs (
  path TEXT PRIMARY KEY,
  file_count INTEGER,
  byte_size INTEGER,
  langs_json TEXT,
  important_json TEXT
);
CREATE TABLE symbols (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  start_line INTEGER,
  end_line INTEGER,
  parent_name TEXT
);
CREATE INDEX symbols_name ON symbols (name);
CREATE TABLE imports (
  from_path TEXT NOT NULL,
  to_spec TEXT NOT NULL,
  to_path TEXT
);
CREATE VIRTUAL TABLE files_fts USING fts5(path, content, tokenize = 'unicode61');
CREATE VIRTUAL TABLE symbols_fts USING fts5(name, path, tokenize = 'unicode61');
CREATE TABLE meta (k TEXT PRIMARY KEY, v TEXT);
`;

export const SCHEMA_VERSION = "1";
