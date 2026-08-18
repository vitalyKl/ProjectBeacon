const LOCAL_ROOT_MAX = 512;

export function parseLocalRootHint(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim().replaceAll("\\", "/");
  if (trimmed.length === 0 || trimmed.length > LOCAL_ROOT_MAX) {
    return undefined;
  }
  if (trimmed.startsWith("/") || /^[A-Za-z]:/.test(trimmed)) {
    return undefined;
  }
  const parts = trimmed.split("/");
  if (parts.some((part) => part === "" || part === "." || part === "..")) {
    return undefined;
  }
  return parts.join("/");
}
