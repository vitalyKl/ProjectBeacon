import { describe, expect, it } from "vitest";
import { containsNul, isDeniedDirName, isDeniedFile } from "./denylist.js";

describe("denylist and binary detection", () => {
  it("skips generated and secret files", () => {
    expect(isDeniedDirName("node_modules")).toBe(true);
    expect(isDeniedDirName(".git")).toBe(true);
    expect(isDeniedDirName("dist")).toBe(true);
    expect(isDeniedDirName(".next")).toBe(true);
    expect(isDeniedFile(".env")).toBe(true);
    expect(isDeniedFile("secret.pem")).toBe(true);
    expect(isDeniedFile("certs/prod.pem")).toBe(true);
    expect(isDeniedFile("src/app.ts")).toBe(false);
  });

  it("marks a buffer as binary when the first 8KB contain NUL", () => {
    const withNul = Buffer.from([0x68, 0x69, 0x00, 0x21]);
    const text = Buffer.from("hello world");
    expect(containsNul(withNul)).toBe(true);
    expect(containsNul(text)).toBe(false);
  });
});
