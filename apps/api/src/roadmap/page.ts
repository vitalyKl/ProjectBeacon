import {
  encodeCursor,
  PAGINATION_DEFAULT_LIMIT,
  PAGINATION_MAX_LIMIT,
  tryDecodeCursor,
  type CursorPayload,
} from "@beacon/shared";
import type { Context } from "hono";

import { errorJson } from "../errors.js";
import type { PageQuery } from "./types.js";

export function parsePageQuery(c: Context): PageQuery | Response {
  const limitRaw = c.req.query("limit");
  let limit = PAGINATION_DEFAULT_LIMIT;
  if (limitRaw !== undefined) {
    if (!/^[0-9]+$/.test(limitRaw)) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_limit" });
    }
    const parsed = Number(limitRaw);
    if (!Number.isInteger(parsed) || parsed < 1 || parsed > PAGINATION_MAX_LIMIT) {
      return errorJson(c, 400, "unauthorized", "invalid limit", { reason: "invalid_limit" });
    }
    limit = parsed;
  }

  const cursorRaw = c.req.query("cursor");
  if (cursorRaw === undefined || cursorRaw === "") {
    return { limit };
  }
  const decoded = tryDecodeCursor(cursorRaw);
  if (!decoded.ok) {
    return errorJson(c, 400, "unauthorized", "invalid cursor", { reason: "invalid_cursor" });
  }
  return { limit, cursor: decoded.value };
}

export function keysetAfter(itemTime: Date, itemId: string, cursor: CursorPayload): boolean {
  const cursorTime = Date.parse(cursor.t);
  const time = itemTime.getTime();
  return time < cursorTime || (time === cursorTime && itemId < cursor.id);
}

export function paginateRecords<T extends { id: string }>(
  records: T[],
  query: PageQuery,
  timestamp: (item: T) => Date,
): { items: T[]; next_cursor: string | null } {
  const sorted = [...records].sort((a, b) => {
    const delta = timestamp(b).getTime() - timestamp(a).getTime();
    if (delta !== 0) {
      return delta;
    }
    return b.id.localeCompare(a.id);
  });

  const filtered = query.cursor
    ? sorted.filter((item) => keysetAfter(timestamp(item), item.id, query.cursor!))
    : sorted;
  const page = filtered.slice(0, query.limit);
  const last = page[page.length - 1];
  const next =
    filtered.length > query.limit && last
      ? encodeCursor({ t: timestamp(last).toISOString(), id: last.id })
      : null;
  return { items: page, next_cursor: next };
}
