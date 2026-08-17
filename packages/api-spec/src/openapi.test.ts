import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { toOpenApi } from "./openapi.js";

describe("toOpenApi", () => {
  it("includes SessionBrief, error, and pagination components", async () => {
    const doc = toOpenApi();
    expect(doc.openapi).toBe("3.1.0");
    expect(doc.components?.schemas?.["SessionBrief"]).toBeDefined();
    expect(doc.components?.schemas?.["ErrorResponse"]).toBeDefined();
    expect(doc.components?.schemas?.["PaginationQuery"]).toBeDefined();
    expect(doc.paths?.["/v1/projects/{id}/context/compile"]).toBeDefined();

    const outPath = resolve(dirname(fileURLToPath(import.meta.url)), "../openapi.json");
    await mkdir(dirname(outPath), { recursive: true });
    await writeFile(outPath, `${JSON.stringify(doc, null, 2)}\n`, "utf8");
  });
});
