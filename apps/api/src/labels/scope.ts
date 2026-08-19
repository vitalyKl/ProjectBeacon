import { compileExtraPaths, uniqueScopePaths, type ScopePath } from "@beacon/context";

import type { LabelRecord } from "./types.js";

export function activeLabelPaths(labels: LabelRecord[]): ScopePath[] {
  return uniqueScopePaths(
    labels
      .filter((label) => label.status === "active")
      .flatMap((label) => label.paths),
  );
}

export function extraCompilePaths(labels: LabelRecord[], repoId?: string | null): string[] {
  return compileExtraPaths(activeLabelPaths(labels), repoId);
}
