import { encodeCursor, PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT, uuidv7 } from "@beacon/shared";
import type { Context } from "hono";
import { describe, expect, it } from "vitest";

import type { ActivityEventRecord } from "../roadmap/types.js";
import {
  isResponse,
  parseIdempotencyKey,
  parseLinkedPaths,
  parsePageQuery,
  parseText,
  writeActivity,
} from "./parse.js";

const REPO_A = "01934567-89ab-7cde-89ab-0123456789aa";
const REPO_B = "01934567-89ab-7cde-89ab-0123456789ab";

function fakeContext(input: {
  query?: Record<string, string | undefined>;
  headers?: Record<string, string | undefined>;
}): Context {
  return {
    req: {
      query: (name: string) => input.query?.[name],
      header: (name: string) => input.headers?.[name],
    },
    json: (body: unknown, status?: number) =>
      new Response(JSON.stringify(body), { status: status ?? 200 }),
  } as unknown as Context;
}

describe("isResponse", () => {
  it("narrows Response instances", () => {
    expect(isResponse(new Response())).toBe(true);
    expect(isResponse({ ok: true })).toBe(false);
    expect(isResponse("no")).toBe(false);
  });
});

describe("parseText", () => {
  it("trims required text and rejects empty or overlong values", () => {
    expect(parseText(undefined, 8)).toBeUndefined();
    expect(parseText(1, 8)).toBeUndefined();
    expect(parseText(" hello ", 8)).toBe("hello");
    expect(parseText("   ", 8)).toBeUndefined();
    expect(parseText("toolong!!", 8)).toBeUndefined();
  });

  it("keeps empty and untrimmed bodies when allowEmpty is set", () => {
    expect(parseText("", 8, true)).toBe("");
    expect(parseText("  ", 8, true)).toBe("  ");
    expect(parseText(" hello ", 8, true)).toBe(" hello ");
    expect(parseText("toolong!!", 8, true)).toBeUndefined();
  });
});

describe("parseIdempotencyKey", () => {
  it("reads a trimmed header and rejects missing or overlong keys", () => {
    expect(parseIdempotencyKey(fakeContext({}))).toBeUndefined();
    expect(parseIdempotencyKey(fakeContext({ headers: { "idempotency-key": "  " } }))).toBeUndefined();
    expect(
      parseIdempotencyKey(fakeContext({ headers: { "idempotency-key": "  abc  " } })),
    ).toBe("abc");
    expect(
      parseIdempotencyKey(fakeContext({ headers: { "idempotency-key": "x".repeat(257) } })),
    ).toBeUndefined();
    expect(
      parseIdempotencyKey(fakeContext({ headers: { "idempotency-key": "x".repeat(256) } })),
    ).toBe("x".repeat(256));
  });
});

describe("parseLinkedPaths", () => {
  it("accepts omitted paths and valid repo/path pairs", () => {
    expect(parseLinkedPaths(undefined)).toEqual({ ok: true, paths: [] });
    expect(
      parseLinkedPaths([
        { repo_id: REPO_A, path: "apps/api/src/http.ts" },
        { repo_id: REPO_B, path: "  apps/web  " },
      ]),
    ).toEqual({
      ok: true,
      paths: [
        { repo_id: REPO_A, path: "apps/api/src/http.ts" },
        { repo_id: REPO_B, path: "apps/web" },
      ],
    });
  });

  it("rejects junk and missing repo_id", () => {
    expect(parseLinkedPaths("x")).toEqual({ ok: false, reason: "invalid" });
    expect(parseLinkedPaths([{ path: "src/a.ts" }])).toEqual({
      ok: false,
      reason: "repo_ambiguous",
    });
    expect(parseLinkedPaths([{ repo_id: REPO_A, path: "" }])).toEqual({
      ok: false,
      reason: "invalid",
    });
    expect(parseLinkedPaths([{ repo_id: "not-a-uuid", path: "src/a.ts" }])).toEqual({
      ok: false,
      reason: "invalid",
    });
  });
});

describe("parsePageQuery", () => {
  it("defaults the limit and accepts a valid cursor", () => {
    expect(parsePageQuery(fakeContext({}))).toEqual({ limit: PAGINATION_DEFAULT_LIMIT });
    const id = uuidv7(Date.parse("2026-01-02T03:04:05.000Z"));
    const cursor = encodeCursor({ t: "2026-01-02T03:04:05.000Z", id });
    expect(parsePageQuery(fakeContext({ query: { limit: "10", cursor } }))).toEqual({
      limit: 10,
      cursor: { t: "2026-01-02T03:04:05.000Z", id },
    });
  });

  it("rejects invalid limit and cursor", async () => {
    const badLimit = parsePageQuery(fakeContext({ query: { limit: "0" } }));
    expect(badLimit).toBeInstanceOf(Response);
    expect((badLimit as Response).status).toBe(400);
    expect(await (badLimit as Response).json()).toMatchObject({
      error: { code: "invalid_request", details: { reason: "invalid_limit" } },
    });

    const overMax = parsePageQuery(
      fakeContext({ query: { limit: String(PAGINATION_MAX_LIMIT + 1) } }),
    );
    expect(overMax).toBeInstanceOf(Response);

    const badCursor = parsePageQuery(fakeContext({ query: { cursor: "nope" } }));
    expect(badCursor).toBeInstanceOf(Response);
    expect(await (badCursor as Response).json()).toMatchObject({
      error: { code: "invalid_request", details: { reason: "invalid_cursor" } },
    });
  });
});

describe("writeActivity", () => {
  it("records actor, verb, and an empty payload by default", async () => {
    const events: ActivityEventRecord[] = [];
    const now = new Date("2026-03-04T05:06:07.000Z");
    await writeActivity(
      {
        writeActivity: async (event) => {
          events.push(event);
          return event;
        },
      },
      { type: "user", id: REPO_A },
      {
        projectId: REPO_B,
        objectType: "task",
        objectId: REPO_A,
        verb: "create",
        now,
      },
    );
    expect(events).toHaveLength(1);
    expect(events[0]).toMatchObject({
      projectId: REPO_B,
      objectType: "task",
      objectId: REPO_A,
      actorType: "user",
      actorId: REPO_A,
      verb: "create",
      payload: {},
      createdAt: now,
    });
    expect(events[0]?.id).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
  });
});
