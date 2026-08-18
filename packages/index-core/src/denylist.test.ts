import { describe, expect, it } from "vitest";
import {
  containsBeaconToken,
  containsNul,
  findBeaconTokenHits,
  isDeniedDirName,
  isDeniedFile,
} from "./denylist.js";

describe("denylist and binary detection", () => {
  it("skips generated and secret files", () => {
    expect(isDeniedDirName("node_modules")).toBe(true);
    expect(isDeniedDirName("Node_Modules")).toBe(true);
    expect(isDeniedDirName(".Git")).toBe(true);
    expect(isDeniedDirName("Dist")).toBe(true);
    expect(isDeniedDirName(".next")).toBe(true);
    expect(isDeniedDirName(".idea")).toBe(true);
    expect(isDeniedFile(".env")).toBe(true);
    expect(isDeniedFile(".ENV")).toBe(true);
    expect(isDeniedFile("secret.pem")).toBe(true);
    expect(isDeniedFile("certs/prod.PEM")).toBe(true);
    expect(isDeniedFile(".npmrc")).toBe(true);
    expect(isDeniedFile("service-account.json")).toBe(true);
    expect(isDeniedFile("id_ed25519")).toBe(true);
    expect(isDeniedFile("src/app.ts")).toBe(false);
  });

  it("detects committed Beacon tokens without matching shorter prefixes", () => {
    const token = `bcn_${"A".repeat(43)}`;
    expect(containsBeaconToken(`export const TOKEN = "${token}";`)).toBe(true);
    expect(findBeaconTokenHits(`prefix ${token} suffix`)).toEqual([{ offset: 7 }]);
    expect(containsBeaconToken("bcn_short")).toBe(false);
    expect(containsBeaconToken("not_a_token")).toBe(false);
  });

  it("marks a buffer as binary when the first 8KB contain NUL", () => {
    const withNul = Buffer.from([0x68, 0x69, 0x00, 0x21]);
    const text = Buffer.from("hello world");
    expect(containsNul(withNul)).toBe(true);
    expect(containsNul(text)).toBe(false);
  });
});
