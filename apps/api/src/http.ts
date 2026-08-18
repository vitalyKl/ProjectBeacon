import type { Context } from "hono";

export async function readJson(c: Context): Promise<unknown> {
  try {
    return await c.req.json();
  } catch {
    return undefined;
  }
}

export async function readObject(c: Context): Promise<Record<string, unknown> | undefined> {
  const body = await readJson(c);
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    return undefined;
  }
  return body as Record<string, unknown>;
}

export function parseOptionalString(value: unknown, max = 256): string | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  if (trimmed.length === 0 || trimmed.length > max) {
    return undefined;
  }
  return trimmed;
}

export function parseEmail(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const email = value.trim();
  if (email.length < 3 || email.length > 320 || !email.includes("@") || /\s/.test(email)) {
    return undefined;
  }
  return email;
}
