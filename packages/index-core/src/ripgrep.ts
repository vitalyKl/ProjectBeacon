import { spawnSync } from "node:child_process";
import { toPosix } from "./paths.js";
import type { ContentHit } from "./types.js";

const RG_GLOBS = [
  "!.git/**",
  "!node_modules/**",
  "!dist/**",
  "!.next/**",
  "!.turbo/**",
  "!coverage/**",
  "!.env",
  "!.env.*",
  "!*.pem",
  "!*.key",
];

export function searchRipgrep(
  repoRoot: string,
  query: string,
  limit: number,
): ContentHit[] | null {
  const args = [
    "--json",
    "--hidden",
    "--max-count",
    String(limit),
    "--max-filesize",
    "1M",
    ...RG_GLOBS.flatMap((glob) => ["--glob", glob]),
    "--",
    query,
  ];
  const result = spawnSync("rg", args, {
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
