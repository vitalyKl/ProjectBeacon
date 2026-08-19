import type { IndexMode } from "./api";

const LOCAL_ROOT_MAX = 512;

export type LocalIndexMode = Extract<IndexMode, "sidecar" | "bind_mount">;

export const LOCAL_INDEX_MODES: LocalIndexMode[] = ["sidecar", "bind_mount"];

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
  if (trimmed === ".") {
    return ".";
  }
  const parts = trimmed.split("/");
  if (parts.some((part) => part === "" || part === "." || part === "..")) {
    return undefined;
  }
  return parts.join("/");
}

export function localRepoAttachInput(
  path: string,
  indexMode: LocalIndexMode,
):
  | {
      ok: true;
      input: {
        provider: "local";
        index_mode: LocalIndexMode;
        local_root_hint: string;
      };
    }
  | { ok: false } {
  const hint = parseLocalRootHint(path);
  if (!hint) {
    return { ok: false };
  }
  return {
    ok: true,
    input: {
      provider: "local",
      index_mode: indexMode,
      local_root_hint: hint,
    },
  };
}
