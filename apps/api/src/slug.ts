const SLUG_RE = /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/;

export function parseSlug(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const slug = value.trim().toLowerCase();
  if (!SLUG_RE.test(slug)) {
    return undefined;
  }
  return slug;
}

export function slugFromLogin(login: string): string {
  const slug = login
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
  if (slug.length === 0) {
    return "user";
  }
  if (slug.length === 1) {
    return slug;
  }
  return slug.replace(/-+$/g, "") || "user";
}

export function slugCandidate(base: string, attempt: number): string {
  if (attempt <= 1) {
    return base.slice(0, 64);
  }
  const suffix = `-${attempt}`;
  return `${base.slice(0, Math.max(1, 64 - suffix.length))}${suffix}`;
}
