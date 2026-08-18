import { NextResponse } from "next/server";

import type { ApiErrorBody } from "@/lib/api";

export const dynamic = "force-dynamic";

function apiUrl(path: string): string {
  const base = (process.env.BEACON_API_URL ?? "http://127.0.0.1:8080").replace(/\/$/, "");
  return `${base}${path}`;
}

function failRedirect(origin: string, reason: string): NextResponse {
  const next = new URL("/login/github/failed", origin);
  next.searchParams.set("reason", reason);
  return NextResponse.redirect(next);
}

async function readMessage(res: Response, fallback: string): Promise<string> {
  try {
    const body = (await res.json()) as ApiErrorBody;
    return body.error?.message ?? fallback;
  } catch {
    return fallback;
  }
}

export async function GET(request: Request) {
  const url = new URL(request.url);
  const code = url.searchParams.get("code")?.trim() ?? "";
  const oauthError = url.searchParams.get("error_description") ?? url.searchParams.get("error");

  if (!code) {
    return failRedirect(
      url.origin,
      oauthError ?? "GitHub did not return a sign-in code. Start again from the homepage.",
    );
  }

  const upstream = await fetch(apiUrl("/v1/auth/github"), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ code }),
    cache: "no-store",
  });

  if (!upstream.ok) {
    return failRedirect(url.origin, await readMessage(upstream, "GitHub sign-in failed"));
  }

  const next = NextResponse.redirect(new URL("/app", url.origin));
  for (const cookie of upstream.headers.getSetCookie()) {
    next.headers.append("set-cookie", cookie);
  }
  return next;
}
