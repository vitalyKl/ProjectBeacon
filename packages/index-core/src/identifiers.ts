const IDENTIFIER = /[A-Za-z_][A-Za-z0-9_]{2,}/g;
const SKIP = new Set(["the", "and", "for", "with", "from", "this", "that", "into", "when", "then"]);

export function extractIdentifiers(...parts: Array<string | null | undefined>): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const part of parts) {
    if (!part) {
      continue;
    }
    const matches = part.match(IDENTIFIER) ?? [];
    for (const match of matches) {
      const key = match.toLowerCase();
      if (SKIP.has(key) || seen.has(key)) {
        continue;
      }
      seen.add(key);
      out.push(match);
    }
  }
  return out;
}
