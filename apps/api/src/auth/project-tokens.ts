import { createHash, randomBytes } from "node:crypto";

export const PROJECT_TOKEN_PREFIX = "bcn_";
export const PROJECT_TOKEN_SECRET_BYTES = 32;
export const PROJECT_TOKEN_LENGTH = 47;
export const PROJECT_TOKEN_DISPLAY_PREFIX_LENGTH = 8;

const DEFAULT_TTL_MS = 90 * 24 * 60 * 60 * 1000;
const TTL_MS = {
  "7d": 7 * 24 * 60 * 60 * 1000,
  "90d": DEFAULT_TTL_MS,
  "1y": 365 * 24 * 60 * 60 * 1000,
} as const;

export type TokenTtl = keyof typeof TTL_MS | "none";

export function generateProjectToken(): string {
  return PROJECT_TOKEN_PREFIX + randomBytes(PROJECT_TOKEN_SECRET_BYTES).toString("base64url");
}

export function hashProjectToken(token: string): Buffer {
  return createHash("sha256").update(token).digest();
}

export function projectTokenDisplayPrefix(token: string): string {
  return token.slice(0, PROJECT_TOKEN_DISPLAY_PREFIX_LENGTH);
}

export function isProjectTokenFormat(token: string): boolean {
  return token.startsWith(PROJECT_TOKEN_PREFIX) && token.length === PROJECT_TOKEN_LENGTH;
}

export function isTokenTtl(value: string): value is TokenTtl {
  return value === "none" || value in TTL_MS;
}

export function tokenExpiresAt(now: Date, ttl: TokenTtl): Date | null {
  if (ttl === "none") {
    return null;
  }
  return new Date(now.getTime() + TTL_MS[ttl]);
}

export const DEFAULT_TOKEN_TTL: TokenTtl = "90d";
