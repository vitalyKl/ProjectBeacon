const IDENTIFIER = /[\p{L}\p{N}_]+/gu;

export function escapeFtsPhrase(term: string): string {
  return `"${term.replaceAll('"', '""')}"`;
}

export function ftsPrefixToken(raw: string): string {
  const token = raw.trim().replace(/[^\p{L}\p{N}_]+/gu, "");
  if (!token) {
    return "";
  }
  return `${escapeFtsPhrase(token)}*`;
}

export function ftsContentQuery(raw: string): string {
  const tokens = raw.match(IDENTIFIER) ?? [];
  if (tokens.length === 0) {
    return escapeFtsPhrase(raw.trim());
  }
  if (tokens.length === 1) {
    return `${escapeFtsPhrase(tokens[0]!)}*`;
  }
  return tokens.map((token) => `${escapeFtsPhrase(token)}*`).join(" AND ");
}

export function escapeLikePrefix(value: string): string {
  return value.replaceAll("\\", "\\\\").replaceAll("%", "\\%").replaceAll("_", "\\_");
}
