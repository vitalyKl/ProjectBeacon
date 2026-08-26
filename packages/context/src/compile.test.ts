import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { jsLengthDiv4, TOKENIZER_ID } from "@beacon/shared";
import { describe, expect, it } from "vitest";

import {
  COMPILER_VERSION,
  compileSessionBrief,
  sessionBriefMarkdown,
  type CompileDocument,
  type CompileNode,
} from "./compile.js";
import {
  COMPILED_AT,
  CONSTRAINT_ID,
  DECISION_ID,
  PATH_NODE_ID,
  PROJECT_ID,
  PROJECT_NODE_ID,
  REPO_ID,
  REVISION_ID,
  TASK_ID,
} from "./fixtures/ids.js";

const fixturesDir = join(dirname(fileURLToPath(import.meta.url)), "fixtures");

function loadFixture<T>(name: string): T {
  return JSON.parse(readFileSync(join(fixturesDir, name), "utf8")) as T;
}

function section(
  id: Exclude<CompileNode["sections"][number]["id"], "custom">,
  body: string,
  ordinal: number,
): CompileNode["sections"][number] {
  return { id, title: id, body_md: body, ordinal };
}

function projectNode(sections: CompileNode["sections"]): CompileNode {
  return {
    id: PROJECT_NODE_ID,
    project_id: PROJECT_ID,
    repo_id: null,
    task_id: null,
    scope_type: "project",
    path: "",
    sections,
  };
}

function pathNode(path: string, sections: CompileNode["sections"]): CompileNode {
  return {
    id: PATH_NODE_ID,
    project_id: PROJECT_ID,
    repo_id: REPO_ID,
    task_id: null,
    scope_type: "path",
    path,
    sections,
  };
}

function document(nodes: CompileNode[], extras?: Partial<CompileDocument>): CompileDocument {
  return {
    project: { id: PROJECT_ID, name: "Beacon", slug: "beacon" },
    nodes,
    constraints: [
      {
        id: CONSTRAINT_ID,
        kind: "security",
        body: "Do not exfiltrate secrets.",
        scope_path: "",
        status: "active",
      },
    ],
    decisions: [
      {
        id: DECISION_ID,
        title: "Zod is the contract",
        status: "accepted",
        decision: "Generate OpenAPI from Zod.",
        related_paths: ["packages/api-spec"],
        superseded_by: null,
      },
    ],
    task: {
      id: TASK_ID,
      title: "Compile session briefs",
      status: "in_progress",
      type: "task",
      milestone_id: null,
      acceptance_md: "Compile succeeds without index extras.",
      how_to_check: "",
      linked_paths: [],
    },
    milestone: null,
    revision_id: REVISION_ID,
    compiled_at: COMPILED_AT,
    ...extras,
  };
}

describe("compileSessionBrief", () => {
  it("lets a deeper path overlay replace a project section of the same id", () => {
    const fixture = loadFixture<{
      input: { project_id: string; repo_id: string; path: string; budget_tokens: number };
      expected: { goals_body: string; source_node_ids: string[] };
    }>("merge.json");
    const { brief } = compileSessionBrief(
      fixture.input,
      document([
        projectNode([
          section("goals", "Ship the product.", 0),
          section("security", "Do not leak tokens.", 1),
        ]),
        pathNode("apps/api", [section("goals", "Ship the compile endpoint.", 0)]),
      ]),
    );
    expect(brief.sections.find((item) => item.id === "goals")?.body_md).toBe(
      fixture.expected.goals_body,
    );
    expect(brief.sources.map((source) => source.node_id)).toEqual(fixture.expected.source_node_ids);
  });

  it("includes the only repo-scope brief when compile has no repo_id", () => {
    const repoNode: CompileNode = {
      id: "018f1e2c-3d4e-7000-8000-000000000014",
      project_id: PROJECT_ID,
      repo_id: REPO_ID,
      task_id: null,
      scope_type: "repo",
      path: "",
      sections: [section("goals", "Imported living brief.", 0)],
    };
    const { brief } = compileSessionBrief({ project_id: PROJECT_ID }, document([repoNode]));
    expect(brief.sections.find((item) => item.id === "goals")?.body_md).toBe(
      "Imported living brief.",
    );
    expect(brief.sources.map((source) => source.node_id)).toEqual([repoNode.id]);
  });

  it("merges extra_paths from attached labels into the selected path nodes", () => {
    const extraNode: CompileNode = {
      id: "018f1e2c-3d4e-7000-8000-000000000013",
      project_id: PROJECT_ID,
      repo_id: REPO_ID,
      task_id: null,
      scope_type: "path",
      path: "apps/web",
      sections: [section("conventions", "Keep the board thin.", 0)],
    };
    const { brief } = compileSessionBrief(
      { project_id: PROJECT_ID, repo_id: REPO_ID, extra_paths: ["apps/web"] },
      document([
        projectNode([
          section("goals", "Ship the product.", 0),
          section("security", "Do not leak tokens.", 1),
        ]),
        extraNode,
      ]),
    );
    expect(brief.sections.find((item) => item.id === "conventions")?.body_md).toBe(
      "Keep the board thin.",
    );
    expect(brief.sources.map((source) => source.node_id)).toContain(extraNode.id);
  });

  it("emits never-drop layers and sets overflow when the budget is tiny", () => {
    const fixture = loadFixture<{
      input: { project_id: string; budget_tokens: number };
      expected: { overflow: boolean; kept_section_ids: string[]; dropped: string[] };
    }>("overflow.json");
    const { brief } = compileSessionBrief(
      fixture.input,
      document([
        projectNode([
          section("non_goals", "No embeddings in v1.", 0),
          section("security", "Do not leak tokens.", 1),
          section("glossary", "Brief means SessionBrief.", 2),
        ]),
      ]),
    );
    expect(brief.budget.overflow).toBe(fixture.expected.overflow);
    expect(brief.sections.map((item) => item.id)).toEqual(fixture.expected.kept_section_ids);
    expect(brief.budget.dropped).toEqual(fixture.expected.dropped);
    expect(brief.constraints).toHaveLength(1);
    expect(brief.task?.title).toBe("Compile session briefs");
    expect(brief.handoff).toBeNull();
    expect(brief.changed_scope).toBeNull();
    expect(brief.tree_capsule).toBeNull();
  });

  it("keeps definition of done when the budget is tiny", () => {
    const { brief } = compileSessionBrief(
      { project_id: PROJECT_ID, budget_tokens: 1 },
      document([
        projectNode([
          section("non_goals", "No embeddings in v1.", 0),
          section("security", "Do not leak tokens.", 1),
          section("definition_of_done", "Tests and typecheck are green.", 2),
          section("glossary", "Brief means SessionBrief.", 3),
        ]),
      ]),
    );
    expect(brief.sections.map((item) => item.id)).toEqual([
      "non_goals",
      "security",
      "definition_of_done",
    ]);
    expect(brief.budget.dropped).toContain("section:glossary");
  });

  it("succeeds with no index extras and lists changed_scope and tree_capsule in dropped", () => {
    const fixture = loadFixture<{
      input: { project_id: string };
      expected: {
        overflow: boolean;
        dropped: string[];
        changed_scope: null;
        tree_capsule: null;
      };
    }>("missing-extras.json");
    const { brief } = compileSessionBrief(
      fixture.input,
      document([
        projectNode([
          section("non_goals", "No embeddings in v1.", 0),
          section("security", "Do not leak tokens.", 1),
        ]),
      ]),
    );
    expect(brief.changed_scope).toBe(fixture.expected.changed_scope);
    expect(brief.tree_capsule).toBe(fixture.expected.tree_capsule);
    expect(brief.budget.dropped).toEqual(fixture.expected.dropped);
    expect(brief.budget.overflow).toBe(fixture.expected.overflow);
    expect(brief.schema_version).toBe("1");
    expect(brief.compiler_version).toBe(COMPILER_VERSION);
  });

  it("attaches extras when the client sends them without include flags", () => {
    const extras = {
      changed_scope: {
        paths: [{ repo_id: REPO_ID, path: "apps/api" }],
        reasons: [{ path: "apps/api", repo_id: REPO_ID, reason: "linked_path" }],
      },
      tree_capsule: {
        repo_id: REPO_ID,
        root: ".",
        entries: [{ path: "apps", kind: "dir" as const }],
      },
    };
    const { brief } = compileSessionBrief(
      { project_id: PROJECT_ID, extras },
      document([
        projectNode([
          section("non_goals", "No embeddings in v1.", 0),
          section("security", "Do not leak tokens.", 1),
        ]),
      ]),
    );
    expect(brief.changed_scope).toEqual(extras.changed_scope);
    expect(brief.tree_capsule).toEqual(extras.tree_capsule);
    expect(brief.budget.dropped).not.toContain("changed_scope");
    expect(brief.budget.dropped).not.toContain("tree_capsule");
  });

  it("omits extras when include flags are explicitly off", () => {
    const { brief } = compileSessionBrief(
      {
        project_id: PROJECT_ID,
        include: { changed_scope: false, tree_capsule: false },
        extras: {
          changed_scope: {
            paths: [{ repo_id: REPO_ID, path: "apps/api" }],
            reasons: [{ path: "apps/api", repo_id: REPO_ID, reason: "linked_path" }],
          },
          tree_capsule: {
            repo_id: REPO_ID,
            root: ".",
            entries: [{ path: "apps", kind: "dir" as const }],
          },
        },
      },
      document([
        projectNode([
          section("non_goals", "No embeddings in v1.", 0),
          section("security", "Do not leak tokens.", 1),
        ]),
      ]),
    );
    expect(brief.changed_scope).toBeNull();
    expect(brief.tree_capsule).toBeNull();
    expect(brief.budget.dropped).toEqual(expect.arrayContaining(["changed_scope", "tree_capsule"]));
  });

  it("never compiles What's next / Next work — the board is the queue", () => {
    const nextWork: CompileNode["sections"][number] = {
      id: "custom",
      key: "next-work",
      title: "Next work",
      body_md: "Pick the next ready item from the board.",
      ordinal: 4,
    };
    const { brief, markdown } = compileSessionBrief(
      { project_id: PROJECT_ID },
      document([
        projectNode([
          section("security", "Do not leak tokens.", 0),
          nextWork,
        ]),
      ], { milestone: null, task: null }),
    );
    expect(brief.sections.some((item) => item.title === "Next work")).toBe(false);
    expect(markdown).not.toContain("## Next work");
  });

  it("includes How to check when the task has verification notes", () => {
    const { markdown } = compileSessionBrief(
      { project_id: PROJECT_ID },
      document([projectNode([section("security", "Do not leak tokens.", 0)])], {
        task: {
          id: TASK_ID,
          title: "Compile session briefs",
          status: "in_progress",
          type: "task",
          milestone_id: null,
          acceptance_md: "Compile succeeds without index extras.",
          how_to_check: "Open /app/tasks and confirm How to check is visible.",
          linked_paths: [],
        },
      }),
    );
    expect(markdown).toContain("## How to check");
    expect(markdown).toContain("Open /app/tasks and confirm How to check is visible.");
  });

  it("estimates tokens with js_length_div_4 of the markdown projection", () => {
    const { brief, markdown } = compileSessionBrief(
      { project_id: PROJECT_ID },
      document([projectNode([section("security", "Do not leak tokens.", 0)])]),
    );
    expect(brief.budget.tokenizer).toBe(TOKENIZER_ID);
    expect(markdown).toBe(sessionBriefMarkdown(brief));
    expect(brief.budget.used_estimate).toBe(jsLengthDiv4(markdown));
    expect(jsLengthDiv4("abcd")).toBe(1);
    expect(jsLengthDiv4("😀")).toBe(1);
  });
});
