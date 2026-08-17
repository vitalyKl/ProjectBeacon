import { describe, expect, it } from "vitest";
import { resolveImport } from "./resolve.js";

describe("import resolver", () => {
  it("maps same-directory JS specs onto TypeScript sources", () => {
    const files = new Set(["src/index.ts", "src/greet.ts", "src/util.js"]);
    expect(resolveImport("src/index.ts", "./greet.js", files, new Map())).toBe("src/greet.ts");
    expect(resolveImport("src/index.ts", "./util.js", files, new Map())).toBe("src/util.js");
    expect(resolveImport("src/index.ts", "left-pad", files, new Map())).toBeNull();
  });
});
