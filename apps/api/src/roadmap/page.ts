import { encodeCursor, type CursorPayload } from "@beacon/shared";

import type { PageQuery } from "./types.js";

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

export function dependencyCursorId(
  fromTaskId: string,
  toTaskId: string,
  type: string,
  createdAt: Date,
): string {
  const bytes = Buffer.alloc(16);
  const ts = BigInt(createdAt.getTime());
  bytes[0] = Number((ts >> 40n) & 0xffn);
  bytes[1] = Number((ts >> 32n) & 0xffn);
  bytes[2] = Number((ts >> 24n) & 0xffn);
  bytes[3] = Number((ts >> 16n) & 0xffn);
  bytes[4] = Number((ts >> 8n) & 0xffn);
  bytes[5] = Number(ts & 0xffn);
  let mix = 0;
  const seed = `${fromTaskId}\0${toTaskId}\0${type}`;
  for (let i = 0; i < seed.length; i += 1) {
    mix = (Math.imul(mix, 33) + seed.charCodeAt(i)) >>> 0;
  }
  for (let i = 6; i < 16; i += 1) {
    mix = (Math.imul(mix, 1664525) + 1013904223) >>> 0;
    bytes[i] = mix & 0xff;
  }
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x70;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
