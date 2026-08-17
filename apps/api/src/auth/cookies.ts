import type { Context } from "hono";
import { deleteCookie, getCookie, setCookie } from "hono/cookie";

import { SESSION_MAX_AGE_SECONDS } from "./tokens.js";

export const SESSION_COOKIE = "beacon_session";

export function readSessionCookie(c: Context): string | undefined {
  return getCookie(c, SESSION_COOKIE);
}

export function writeSessionCookie(c: Context, token: string, secure: boolean): void {
  setCookie(c, SESSION_COOKIE, token, {
    path: "/",
    httpOnly: true,
    secure,
    sameSite: "Lax",
    maxAge: SESSION_MAX_AGE_SECONDS,
  });
}

export function clearSessionCookie(c: Context, secure: boolean): void {
  deleteCookie(c, SESSION_COOKIE, {
    path: "/",
    httpOnly: true,
    secure,
    sameSite: "Lax",
  });
}
