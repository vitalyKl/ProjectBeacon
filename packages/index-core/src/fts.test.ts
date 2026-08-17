import { describe, expect, it } from "vitest";
import { escapeLikePrefix, ftsContentQuery, ftsPrefixToken } from "./fts.js";

describe("FTS and LIKE helpers", () => {
  it("quotes reserved FTS5 words in prefix queries", () => {
    expect(ftsPrefixToken("AND")).toBe('"AND"*');
    expect(ftsPrefixToken("OR")).toBe('"OR"*');
    expect(ftsPrefixToken("NOT")).toBe('"NOT"*');
    expect(ftsPrefixToken("greet")).toBe('"greet"*');
  });

  it("builds per-token prefix MATCH for multi-word content", () => {
    expect(ftsContentQuery("hello world")).toBe('"hello"* AND "world"*');
    expect(ftsContentQuery("hello-world")).toBe('"hello"* AND "world"*');
  });

  it("escapes LIKE wildcards in prefixes", () => {
    expect(escapeLikePrefix("src/_")).toBe("src/\\_");
    expect(escapeLikePrefix("src/%")).toBe("src/\\%");
    expect(escapeLikePrefix("src\\lib")).toBe("src\\\\lib");
  });
});
