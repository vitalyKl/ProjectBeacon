import { generateKeyPairSync } from "node:crypto";

import { describe, expect, it } from "vitest";

import { createInstallationToken, normalizeCloneRemote, signGithubAppJwt } from "./github-app.js";

describe("github app jwt", () => {
  it("signs an RS256 JWT and mints an installation token", async () => {
    const { privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
    const pem = privateKey.export({ type: "pkcs8", format: "pem" }).toString();
    const jwt = signGithubAppJwt({ appId: "123", privateKey: pem }, 1_700_000_000_000);
    expect(jwt.split(".")).toHaveLength(3);

    const fetchImpl: typeof fetch = async (input, init) => {
      expect(String(input)).toBe("https://api.github.com/app/installations/99/access_tokens");
      expect(init?.method).toBe("POST");
      expect(new Headers(init?.headers).get("authorization")).toMatch(/^Bearer /);
      return Response.json({ token: "ghs_test" });
    };
    await expect(
      createInstallationToken({ appId: "123", privateKey: pem }, "99", fetchImpl),
    ).resolves.toBe("ghs_test");
  });

  it("normalizes a tokenless clone remote", () => {
    expect(normalizeCloneRemote("https://github.com/acme/demo")).toBe(
      "https://github.com/acme/demo.git",
    );
    expect(normalizeCloneRemote("https://x-access-token:ghs_secret@github.com/acme/demo.git")).toBe(
      "https://github.com/acme/demo.git",
    );
  });
});
