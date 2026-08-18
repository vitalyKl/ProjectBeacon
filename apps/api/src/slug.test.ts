import { describe, expect, it } from "vitest";

import { parseSlug, slugCandidate, slugFromLogin } from "./slug.js";

describe("slug helpers", () => {
  it("derives a slug from a login", () => {
    expect(slugFromLogin("Alice.Dev")).toBe("alice-dev");
    expect(slugFromLogin("___")).toBe("user");
    expect(parseSlug("Acme-Org")).toBe("acme-org");
    expect(parseSlug("no spaces")).toBeUndefined();
    expect(slugCandidate("acme", 2)).toBe("acme-2");
  });
});
