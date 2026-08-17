import { spawnSync } from "node:child_process";
import { toPosix } from "./paths.js";
import type { ContentHit } from "./types.js";

export function searchRipgrep(
  repoRoot: string,
  query: string,
  limit: number,
): ContentHit[] | null {
  const result = spawnSync("rg", ["--json", "--max-count", "1", "-m", String(limit), "--", query], {
    cwd: repoRoot,
    encoding: "utf8",
    timeout: 750,
    windowsHide: true,
  });
  if (result.error || result.status === null) {
    return null;
  }
  if (!result.stdout) {
    return [];
  }
  const hits: ContentHit[] = [];
  for (const line of result.stdout.split("\n")) {
    if (!line || hits.length >= limit) {
      break;
    }
    try {
      const event = JSON.parse(line) as {
        type?: string;
        data?: { path?: { text?: string }; lines?: { text?: string } };
      };
      if (event.type !== "match" || !event.data?.path?.text) {
        continue;
      }
      hits.push({
        path: toPosix(event.data.path.text),
        snippet: event.data.lines?.text?.trim(),
        source: "ripgrep",
      });
    } catch {
      continue;
    }
  }
  return hits;
}
