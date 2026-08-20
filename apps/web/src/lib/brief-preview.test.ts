import { describe, expect, it } from "vitest";

import { briefDroppedItems, briefPreviewSectionKey } from "./brief-preview";

describe("briefPreviewSectionKey", () => {
  it("prefers id and key, then ordinal, then index", () => {
    expect(
      briefPreviewSectionKey({ id: "goals", title: "Goals", body_md: "Ship it.", key: "goals" }, 0),
    ).toBe("goals:goals");
    expect(briefPreviewSectionKey({ title: "Custom", body_md: "Note.", ordinal: 4 }, 1)).toBe(
      "Custom:4",
    );
    expect(briefPreviewSectionKey({ title: "Untitled", body_md: "" }, 2)).toBe("Untitled:2");
  });

  it("keeps stored titles as written", () => {
    const title = "Definition of Done";
    expect(briefPreviewSectionKey({ title, body_md: "Tests are green." }, 0)).toContain(title);
  });
});

describe("briefDroppedItems", () => {
  it("copies dropped extras and treats missing as none", () => {
    expect(briefDroppedItems(["glossary", "ownership"])).toEqual(["glossary", "ownership"]);
    expect(briefDroppedItems([])).toEqual([]);
    expect(briefDroppedItems(null)).toEqual([]);
    expect(briefDroppedItems(undefined)).toEqual([]);
  });
});
