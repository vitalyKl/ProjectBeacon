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
    expect(doc.components?.schemas?.["Comment"]).toBeDefined();
    expect(doc.components?.schemas?.["CommentPage"]).toBeDefined();
    expect(doc.paths?.["/v1/projects/{id}/context/compile"]).toBeDefined();
    expect(doc.paths?.["/v1/tasks/{id}/comments"]).toBeDefined();

    const pagination = doc.components.schemas["PaginationQuery"] as {
      required?: string[];
      properties?: { limit?: { default?: number } };
    };
    expect(pagination.required ?? []).not.toContain("limit");
    expect(pagination.properties?.limit?.default).toBe(50);

    const sessionBrief = JSON.stringify(doc.components.schemas["SessionBrief"]);
    expect(sessionBrief).toContain('"const":"custom"');
    expect(sessionBrief).toMatch(/"required":\[[^\]]*"key"/);

    const outPath = resolve(dirname(fileURLToPath(import.meta.url)), "../openapi.json");
    await mkdir(dirname(outPath), { recursive: true });
    await writeFile(outPath, `${JSON.stringify(doc, null, 2)}\n`, "utf8");
  });
});
