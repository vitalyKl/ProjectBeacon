export const SCOPES = [
  "project:read",
  "project:write",
  "context:read",
  "context:write",
  "tasks:read",
  "tasks:write",
  "tasks:delete",
  "decisions:read",
  "decisions:write",
  "constraints:apply",
  "code:read",
  "sessions:write",
  "integrations:write",
  "admin",
] as const;

export type Scope = (typeof SCOPES)[number];

const SCOPE_SET: ReadonlySet<string> = new Set(SCOPES);

export function isScope(value: string): value is Scope {
  return SCOPE_SET.has(value);
}
