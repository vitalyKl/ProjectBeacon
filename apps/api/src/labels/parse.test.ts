import { describe, expect, it } from "vitest";

import { parseLabelIds } from "./parse.js";

const LABEL_A = "01934567-89ab-7cde-89ab-0123456789aa";
const LABEL_B = "01934567-89ab-7cde-89ab-0123456789ab";

describe("parseLabelIds", () => {
  it("dedupes valid ids and rejects junk", () => {
    expect(parseLabelIds([LABEL_A, LABEL_A, LABEL_B])).toEqual({
      ok: true,
      ids: [LABEL_A, LABEL_B],
    });
    expect(parseLabelIds([])).toEqual({ ok: true, ids: [] });
    expect(parseLabelIds(["not-a-uuid"])).toEqual({ ok: false });
    expect(parseLabelIds("x")).toEqual({ ok: false });
  });
});
