import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

function walkTs(dir: string): string[] {
  const out: string[] = [];
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      out.push(...walkTs(full));
      continue;
    }
    if (name.endsWith(".ts")) {
      out.push(full);
    }
  }
  return out;
}

const AUTH_STORE_IMPORT =
  /(?:import|export)\s+(?:type\s+)?(?:\{[^}]*\bAuthStore\b[^}]*\}|\*\s+as\s+\w+)\s+from\s+["'][^"']*auth\/store(?:\.js)?["']/;

describe("AuthStore ports", () => {
  it("is not imported outside auth/", () => {
    const offenders: string[] = [];
    for (const file of walkTs(srcRoot)) {
      const rel = relative(srcRoot, file).split(sep).join("/");
      if (rel === "auth/store.ts" || rel.startsWith("auth/")) {
        continue;
      }
      const source = readFileSync(file, "utf8")
        .replace(/\/\*[\s\S]*?\*\//g, "")
        .replace(/\/\/.*$/gm, "");
      if (AUTH_STORE_IMPORT.test(source)) {
        offenders.push(rel);
      }
    }
    expect(offenders).toEqual([]);
  });
});
