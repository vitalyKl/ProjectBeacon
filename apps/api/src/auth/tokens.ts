import { createHash, randomBytes, timingSafeEqual } from "node:crypto";

export const SESSION_TOKEN_BYTES = 32;
export const SESSION_MAX_AGE_SECONDS = 14 * 24 * 60 * 60;
export const SESSION_TTL_MS = SESSION_MAX_AGE_SECONDS * 1000;
export const ROLLING_REFRESH_AFTER_MS = 60 * 60 * 1000;

export function generateSessionToken(): string {
  return randomBytes(SESSION_TOKEN_BYTES).toString("base64url");
}

export function hashSessionToken(token: string): Buffer {
  return createHash("sha256").update(token).digest();
}

export function parseBearer(header: string | undefined): string | undefined {
  if (!header) {
    return undefined;
  }
  const prefix = "Bearer ";
  if (!header.startsWith(prefix)) {
    return undefined;
  }
  const token = header.slice(prefix.length).trim();
  return token.length > 0 ? token : undefined;
}

export function tokenEquals(expected: string | undefined, provided: string | undefined): boolean {
  if (expected === undefined || expected.length === 0 || provided === undefined) {
    return false;
  }
  const left = createHash("sha256").update(expected).digest();
  const right = createHash("sha256").update(provided).digest();
  return timingSafeEqual(left, right);
}
