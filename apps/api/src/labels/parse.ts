import { isUuid } from "@beacon/shared";

export function parseLabelIds(
  value: unknown,
): { ok: true; ids: string[] } | { ok: false } {
  if (!Array.isArray(value) || value.some((item) => typeof item !== "string" || !isUuid(item))) {
    return { ok: false };
  }
  const unique: string[] = [];
  const seen = new Set<string>();
  for (const id of value as string[]) {
    if (seen.has(id)) {
      continue;
    }
    seen.add(id);
    unique.push(id);
  }
  return { ok: true, ids: unique };
}
