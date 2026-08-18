import { incRateLimited } from "@beacon/shared";
import type { Context } from "hono";

import { errorJson } from "../errors.js";
import type { AuthStore } from "./store.js";

export type RateLimitName = "overall" | "code" | "compile" | "bytes";
export type RateActorKind = "token" | "user";

export type RateLimitConfig = {
  humanPerMin: number;
  tokenPerMin: number;
  codePerMin: number;
  getFileBytesPerMin: number;
  compilePerMin: number;
  burstMultiplier: number;
};

export const DEFAULT_RATE_LIMITS: RateLimitConfig = {
  humanPerMin: 300,
  tokenPerMin: 120,
  codePerMin: 30,
  getFileBytesPerMin: 256 * 1024,
  compilePerMin: 60,
  burstMultiplier: 2,
};

const WINDOW_1M_MS = 60_000;
const WINDOW_10S_MS = 10_000;

export function actorRateKey(kind: RateActorKind, id: string): string {
  return kind === "token" ? `tok_${id}` : `usr_${id}`;
}

export function rateBucketKey(
  actorKey: string,
  limitName: RateLimitName,
  window: "1m" | "10s",
): string {
  return `${actorKey}:${limitName}:${window}`;
}

export function alignedWindowStart(now: Date, windowMs: number): Date {
  return new Date(Math.floor(now.getTime() / windowMs) * windowMs);
}

function perMinuteCap(limits: RateLimitConfig, kind: RateActorKind, name: RateLimitName): number {
  switch (name) {
    case "overall":
      return kind === "token" ? limits.tokenPerMin : limits.humanPerMin;
    case "code":
      return limits.codePerMin;
    case "compile":
      return limits.compilePerMin;
    case "bytes":
      return limits.getFileBytesPerMin;
  }
}

function retryAfterSeconds(now: Date, windowStart: Date, windowMs: number): number {
  const remainingMs = windowStart.getTime() + windowMs - now.getTime();
  return Math.max(1, Math.ceil(remainingMs / 1000));
}

export type ConsumeRateResult = { ok: true } | { ok: false; retryAfter: number };

export async function consumeRateLimit(
  store: AuthStore,
  kind: RateActorKind,
  actorId: string,
  limits: RateLimitConfig,
  now: Date,
  name: RateLimitName,
  delta: { count?: number; bytes?: number } = {},
): Promise<ConsumeRateResult> {
  const countDelta = name === "bytes" ? 0 : (delta.count ?? 1);
  const bytesDelta = name === "bytes" ? (delta.bytes ?? 0) : 0;
  const perMin = perMinuteCap(limits, kind, name);
  const burst = perMin * limits.burstMultiplier;
  const actorKey = actorRateKey(kind, actorId);

  const window1m = alignedWindowStart(now, WINDOW_1M_MS);
  const window10s = alignedWindowStart(now, WINDOW_10S_MS);

  const minute = await store.consumeRateBucket({
    bucketKey: rateBucketKey(actorKey, name, "1m"),
    windowStart: window1m,
    countDelta,
    bytesDelta,
  });
  const burstRow = await store.consumeRateBucket({
    bucketKey: rateBucketKey(actorKey, name, "10s"),
    windowStart: window10s,
    countDelta,
    bytesDelta,
  });

  const minuteValue = name === "bytes" ? Number(minute.bytes) : minute.count;
  const burstValue = name === "bytes" ? Number(burstRow.bytes) : burstRow.count;

  let retryAfter = 0;
  if (minuteValue > perMin) {
    retryAfter = Math.max(retryAfter, retryAfterSeconds(now, window1m, WINDOW_1M_MS));
  }
  if (burstValue > burst) {
    retryAfter = Math.max(retryAfter, retryAfterSeconds(now, window10s, WINDOW_10S_MS));
  }
  if (retryAfter > 0) {
    return { ok: false, retryAfter };
  }
  return { ok: true };
}

export async function enforceRateLimit(
  c: Context,
  store: AuthStore,
  kind: RateActorKind,
  actorId: string,
  limits: RateLimitConfig,
  now: Date,
  name: RateLimitName,
  delta: { count?: number; bytes?: number } = {},
): Promise<Response | undefined> {
  const result = await consumeRateLimit(store, kind, actorId, limits, now, name, delta);
  if (result.ok) {
    return undefined;
  }
  incRateLimited(name);
  c.header("Retry-After", String(result.retryAfter));
  return errorJson(c, 429, "rate_limited", "rate limit exceeded");
}
