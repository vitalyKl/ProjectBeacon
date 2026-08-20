import {
  isUuid,
  PAGINATION_DEFAULT_LIMIT,
  PAGINATION_MAX_LIMIT,
  tryDecodeCursor,
  uuidv7,
} from "@beacon/shared";
import type { Context } from "hono";

import { errorJson } from "../errors.js";
import { parseOptionalString } from "../http.js";
import type { ActivityEventRecord, LinkedPath, PageQuery } from "../roadmap/types.js";

export function isResponse<T>(value: T | Response): value is Response {
  return value instanceof Response;
}

export function parseIdempotencyKey(c: Context): string | undefined {
  const header = c.req.header("idempotency-key")?.trim();
  if (!header || header.length > 256) {
    return undefined;
  }
  return header;
}

/** Checks untrimmed length and can keep empty bodies; parseOptionalString always trims and rejects empty. */
export function parseText(value: unknown, max: number, allowEmpty = false): string | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== "string" || value.length > max) {
    return undefined;
  }
  if (!allowEmpty && value.trim().length === 0) {
    return undefined;
  }
  return allowEmpty ? value : value.trim();
}

export function parseLinkedPaths(
  value: unknown,
): { ok: true; paths: LinkedPath[] } | { ok: false; reason: "invalid" | "repo_ambiguous" } {
  if (value === undefined) {
    return { ok: true, paths: [] };
  }
  if (!Array.isArray(value)) {
    return { ok: false, reason: "invalid" };
  }
  const paths: LinkedPath[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return { ok: false, reason: "invalid" };
    }
    const record = item as Record<string, unknown>;
    const path = parseOptionalString(record["path"], 1024);
    if (!path) {
      return { ok: false, reason: "invalid" };
    }
    const repoId = record["repo_id"];
    if (repoId === undefined || repoId === null) {
      return { ok: false, reason: "repo_ambiguous" };
    }
    if (typeof repoId !== "string" || !isUuid(repoId)) {
      return { ok: false, reason: "invalid" };
    }
    paths.push({ repo_id: repoId, path });
  }
  return { ok: true, paths };
}

export function parsePageQuery(c: Context): PageQuery | Response {
  const limitRaw = c.req.query("limit");
  let limit = PAGINATION_DEFAULT_LIMIT;
  if (limitRaw !== undefined) {
    if (!/^[0-9]+$/.test(limitRaw)) {
      return errorJson(c, 400, "invalid_request", "invalid limit", { reason: "invalid_limit" });
    }
    const parsed = Number(limitRaw);
    if (!Number.isInteger(parsed) || parsed < 1 || parsed > PAGINATION_MAX_LIMIT) {
      return errorJson(c, 400, "invalid_request", "invalid limit", { reason: "invalid_limit" });
    }
    limit = parsed;
  }

  const cursorRaw = c.req.query("cursor");
  if (cursorRaw === undefined || cursorRaw === "") {
    return { limit };
  }
  const decoded = tryDecodeCursor(cursorRaw);
  if (!decoded.ok) {
    return errorJson(c, 400, "invalid_request", "invalid cursor", { reason: "invalid_cursor" });
  }
  return { limit, cursor: decoded.value };
}

export async function writeActivity(
  store: { writeActivity: (event: ActivityEventRecord) => Promise<unknown> },
  actor: { type: string; id: string },
  input: {
    projectId: string;
    objectType: string;
    objectId: string;
    verb: string;
    payload?: Record<string, unknown>;
    now: Date;
  },
): Promise<void> {
  await store.writeActivity({
    id: uuidv7(input.now.getTime()),
    projectId: input.projectId,
    objectType: input.objectType,
    objectId: input.objectId,
    actorType: actor.type,
    actorId: actor.id,
    verb: input.verb,
    payload: input.payload ?? {},
    createdAt: input.now,
  });
}
