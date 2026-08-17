import { describe, expect, it } from "vitest";

import { jsLengthDiv4, TOKENIZER_ID } from "./tokenizer.js";

describe("jsLengthDiv4", () => {
  it("returns 0 for the empty string", () => {
    expect(jsLengthDiv4("")).toBe(0);
  });

  it("estimates from UTF-16 code units", () => {
    expect(jsLengthDiv4("abcd")).toBe(1);
    expect(jsLengthDiv4("abcde")).toBe(2);
    expect(jsLengthDiv4("a".repeat(8))).toBe(2);
    expect(jsLengthDiv4("😀")).toBe(1);
    expect(TOKENIZER_ID).toBe("js_length_div_4");
  });
});
