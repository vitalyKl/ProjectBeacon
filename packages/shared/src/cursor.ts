import { Buffer } from "node:buffer";

import { isUuid } from "./ids.js";

export type CursorPayload = {
  t: string;
  id: string;
};

const ISO_TIMESTAMP_RE =
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$/;

const BASE64URL_RE = /^[A-Za-z0-9_-]+$/;

export class CursorError extends Error {
  override readonly name = "CursorError";

  constructor(message: string) {
    super(message);
  }
}

export function isIsoTimestamp(value: string): boolean {
  return ISO_TIMESTAMP_RE.test(value) && !Number.isNaN(Date.parse(value));
}

export function encodeCursor(payload: CursorPayload): string {
  if (!isIsoTimestamp(payload.t)) {
    throw new CursorError("cursor timestamp is not ISO-8601");
  }
  if (!isUuid(payload.id)) {
    throw new CursorError("cursor id is not a uuid");
  }
  return Buffer.from(JSON.stringify({ t: payload.t, id: payload.id }), "utf8").toString("base64url");
}

export function decodeCursor(cursor: string): CursorPayload {
  if (cursor.length === 0 || !BASE64URL_RE.test(cursor)) {
    throw new CursorError("cursor is not base64url");
  }

  const bytes = Buffer.from(cursor, "base64url");
  if (bytes.toString("base64url") !== cursor) {
    throw new CursorError("cursor is not valid base64url");
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new CursorError("cursor is not valid JSON");
  }

  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new CursorError("cursor payload must be an object");
  }

  const record = parsed as Record<string, unknown>;
  const t = record["t"];
  const id = record["id"];
  if (typeof t !== "string" || !isIsoTimestamp(t)) {
    throw new CursorError("cursor timestamp is not ISO-8601");
  }
  if (typeof id !== "string" || !isUuid(id)) {
    throw new CursorError("cursor id is not a uuid");
  }

  return { t, id };
}

export type CursorDecodeResult =
  | { ok: true; value: CursorPayload }
  | { ok: false; error: CursorError };

export function tryDecodeCursor(cursor: string): CursorDecodeResult {
  try {
    return { ok: true, value: decodeCursor(cursor) };
  } catch (error) {
    if (error instanceof CursorError) {
      return { ok: false, error };
    }
    throw error;
  }
}
