import type { ErrorCode } from "@beacon/shared";
import type { Context } from "hono";
import type { ContentfulStatusCode } from "hono/utils/http-status";

export function errorJson(
  c: Context,
  status: ContentfulStatusCode,
  code: ErrorCode,
  message: string,
  details: Record<string, unknown> = {},
) {
  return c.json({ error: { code, message, details } }, status);
}
