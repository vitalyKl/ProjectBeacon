import { describe, expect, it } from "vitest";

import { CursorError, decodeCursor, encodeCursor, tryDecodeCursor } from "./cursor.js";

const PAYLOAD = {
  t: "2026-08-17T12:00:00.000Z",
  id: "018f1e2c-3d4e-7000-8000-000000000001",
};

describe("cursor codec", () => {
  it("roundtrips a keyset cursor", () => {
    const encoded = encodeCursor(PAYLOAD);
    expect(encoded).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(encoded.includes("+")).toBe(false);
    expect(encoded.includes("/")).toBe(false);
    expect(encoded.includes("=")).toBe(false);
    expect(decodeCursor(encoded)).toEqual(PAYLOAD);
    expect(tryDecodeCursor(encoded)).toEqual({ ok: true, value: PAYLOAD });
  });

  it("rejects malformed cursors", () => {
    const cases = [
      "",
      "%%%",
      "not-json",
      Buffer.from("[]", "utf8").toString("base64url"),
      Buffer.from("null", "utf8").toString("base64url"),
      Buffer.from(JSON.stringify({ id: PAYLOAD.id }), "utf8").toString("base64url"),
      Buffer.from(JSON.stringify({ t: PAYLOAD.t }), "utf8").toString("base64url"),
      Buffer.from(JSON.stringify({ t: "yesterday", id: PAYLOAD.id }), "utf8").toString("base64url"),
      Buffer.from(JSON.stringify({ t: PAYLOAD.t, id: "not-a-uuid" }), "utf8").toString("base64url"),
      Buffer.from(
        JSON.stringify({ t: PAYLOAD.t, id: "018f1e2c-3d4e-4000-8000-000000000001" }),
        "utf8",
      ).toString("base64url"),
    ];

    for (const cursor of cases) {
      expect(() => decodeCursor(cursor)).toThrow(CursorError);
      const result = tryDecodeCursor(cursor);
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.error).toBeInstanceOf(CursorError);
      }
    }
  });
});
