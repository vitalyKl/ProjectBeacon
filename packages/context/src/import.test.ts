import { describe, expect, it } from "vitest";

import { sectionIdForHeading } from "./aliases.js";
import { exportAgentsMd } from "./export.js";
import { parseImportFiles } from "./import.js";

describe("sectionIdForHeading", () => {
  it("maps the documented aliases and unknown headings to custom", () => {
    expect(sectionIdForHeading("Goals")).toEqual({ id: "goals" });
    expect(sectionIdForHeading("Product goals")).toEqual({ id: "goals" });
    expect(sectionIdForHeading("Non-Goals")).toEqual({ id: "non_goals" });
    expect(sectionIdForHeading("Out of scope")).toEqual({ id: "non_goals" });
    expect(sectionIdForHeading("Architecture")).toEqual({ id: "architecture" });
    expect(sectionIdForHeading("System design")).toEqual({ id: "architecture" });
    expect(sectionIdForHeading("Conventions")).toEqual({ id: "conventions" });
    expect(sectionIdForHeading("Style")).toEqual({ id: "style" });
    expect(sectionIdForHeading("Code style")).toEqual({ id: "style" });
    expect(sectionIdForHeading("Commands")).toEqual({ id: "commands" });
    expect(sectionIdForHeading("Development")).toEqual({ id: "commands" });
    expect(sectionIdForHeading("Security")).toEqual({ id: "security" });
    expect(sectionIdForHeading("Pitfalls")).toEqual({ id: "pitfalls" });
    expect(sectionIdForHeading("Gotchas")).toEqual({ id: "pitfalls" });
    expect(sectionIdForHeading("Do not")).toEqual({ id: "pitfalls" });
    expect(sectionIdForHeading("Stack")).toEqual({ id: "stack" });
    expect(sectionIdForHeading("Tech stack")).toEqual({ id: "stack" });
    expect(sectionIdForHeading("Definition of Done")).toEqual({ id: "definition_of_done" });
    expect(sectionIdForHeading("Acceptance criteria")).toEqual({ id: "definition_of_done" });
    expect(sectionIdForHeading("Release checklist")).toEqual({
      id: "custom",
      key: "release-checklist",
    });
  });
});

describe("parseImportFiles", () => {
  it("parses markdown families, cursor/grok rules, CODEOWNERS, and cheap stack detectors", () => {
    const result = parseImportFiles([
      {
        path: "AGENTS.md",
        content: `# Beacon\n\nIntro.\n\n## Goals\nShip the compiler.\n\n## Non-Goals\nNo embeddings.\n\n## Release checklist\nTag after review.\n`,
      },
      {
        path: "apps/api/CONVENTIONS.md",
        content: `## Conventions\nUse Hono routes.\n`,
      },
      {
        path: ".cursor/rules/api.mdc",
        content: `---\ndescription: API package rules\nglobs:\n  - apps/api\n---\n## Commands\npnpm --filter @beacon/api test\n`,
      },
      {
        path: ".grok/rules",
        content: `---\nglobs: ["packages/context"]\n---\nKeep parsers pure.\n`,
      },
      {
        path: "CODEOWNERS",
        content: `# owners\n* @beacon/core\napps/api/ @beacon/api\n`,
      },
      {
        path: "package.json",
        content: JSON.stringify({
          name: "project-beacon",
          packageManager: "pnpm@10",
          devDependencies: { typescript: "5", vitest: "3" },
        }),
      },
      {
        path: "pnpm-workspace.yaml",
        content: "packages:\n  - \"apps/*\"\n  - \"packages/*\"\n",
      },
      {
        path: "ignored.txt",
        content: "not imported",
      },
    ]);

    const agents = result.nodes.find((node) => node.source_path === "AGENTS.md");
    expect(agents?.source).toBe("imported_agents_md");
    expect(agents?.scope_type).toBe("repo");
    expect(agents?.sections.map((section) => section.id)).toEqual([
      "custom",
      "goals",
      "non_goals",
      "custom",
    ]);

    const conventions = result.nodes.find((node) => node.source === "imported_conventions_md");
    expect(conventions).toMatchObject({
      scope_type: "path",
      path: "apps/api",
      source: "imported_conventions_md",
    });

    const cursor = result.nodes.find((node) => node.source === "imported_cursor");
    expect(cursor).toMatchObject({ scope_type: "path", path: "apps/api" });

    const grok = result.nodes.find((node) => node.source === "imported_grok");
    expect(grok).toMatchObject({ scope_type: "path", path: "packages/context" });

    expect(result.code_owners).toEqual([
      { path_pattern: "*", owners: ["@beacon/core"], source: "codeowners" },
      { path_pattern: "apps/api/", owners: ["@beacon/api"], source: "codeowners" },
    ]);
    expect(result.nodes.some((node) => node.sections.some((section) => section.id === "ownership"))).toBe(
      true,
    );

    const stack = result.nodes.filter((node) => node.sections.some((section) => section.id === "stack"));
    expect(stack.length).toBeGreaterThan(0);
    expect(stack.some((node) => node.path === "")).toBe(true);
  });
});

describe("exportAgentsMd", () => {
  it("writes deterministic frontmatter and heading order", () => {
    const markdown = exportAgentsMd({
      revision: "rev-1",
      scope: { repo_id: null, path: "" },
      sections: [
        { id: "goals", title: "Goals", body_md: "Ship it.", ordinal: 0 },
        { id: "security", title: "Security", body_md: "Do not leak tokens.", ordinal: 1 },
      ],
    });
    expect(markdown).toBe(
      [
        "---",
        "managed-by: projectbeacon",
        "revision: rev-1",
        "scope: project",
        "---",
        "",
        "## Goals",
        "",
        "Ship it.",
        "",
        "## Security",
        "",
        "Do not leak tokens.",
        "",
      ].join("\n"),
    );
  });
});
