import { createHmac } from "node:crypto";

import { describe, expect, it } from "vitest";

import { verifyGithubWebhookSignature } from "./signature.js";

describe("verifyGithubWebhookSignature", () => {
  it("accepts a matching sha256 HMAC and rejects mismatches", () => {
    const body = `{"action":"opened"}`;
    const secret = "s3cret";
    const signature = `sha256=${createHmac("sha256", secret).update(body).digest("hex")}`;
    expect(verifyGithubWebhookSignature(body, signature, secret)).toBe(true);
    expect(verifyGithubWebhookSignature(body, signature, "other")).toBe(false);
    expect(verifyGithubWebhookSignature(body, "sha1=abc", secret)).toBe(false);
    expect(verifyGithubWebhookSignature(body, undefined, secret)).toBe(false);
  });
});
