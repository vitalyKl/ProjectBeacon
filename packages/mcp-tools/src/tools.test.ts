import { describe, expect, it } from "vitest";

import {
  CODE_TOOLS,
  GITHUB_TOOLS,
  IDEMPOTENT_TOOLS,
  TOOL_ARG_SCHEMAS,
  TOOL_NAMES,
  getToolDefinition,
  isToolName,
  listToolDefinitions,
  parseToolArgs,
} from "./tools.js";

const REQUIRED_TOOLS = [
  "get_project",
  "get_context_pack",
  "search_context",
  "get_task_brief",
  "list_milestones",
  "list_tasks",
  "get_task",
  "create_task",
  "update_task",
  "list_comments",
  "add_comment",
  "set_status",
  "link_dependency",
  "list_decisions",
  "record_decision",
  "list_labels",
  "propose_label",
  "set_task_labels",
  "get_constraints",
  "create_constraint",
  "apply_constraint",
  "get_tree",
  "search_code",
  "get_file",
  "get_symbol",
  "get_owners",
  "get_related_files",
  "get_changed_scope",
  "start_work",
  "finish_work",
  "get_handoff",
  "github_list_prs",
  "github_list_issues",
  "github_link_issue",
  "github_sync_now",
  "list_reports",
  "generate_report",
  "get_report",
  "list_reviews",
  "import_review",
  "get_review",
] as const;

const TASK_ID = "01934567-89ab-7cde-89ab-0123456789ab";
const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";

describe("tool catalog", () => {
  it("includes every normative tool name", () => {
    expect([...TOOL_NAMES].sort()).toEqual([...REQUIRED_TOOLS].sort());
    for (const name of REQUIRED_TOOLS) {
      expect(isToolName(name)).toBe(true);
      expect(TOOL_ARG_SCHEMAS[name]).toBeDefined();
    }
    expect(
      listToolDefinitions()
        .map((tool) => tool.name)
        .sort(),
    ).toEqual([...REQUIRED_TOOLS].sort());
  });

  it("does not expose run_shell, code:write, or write_handoff", () => {
    expect(TOOL_NAMES).not.toContain("run_shell");
    expect(TOOL_NAMES).not.toContain("code:write");
    expect(TOOL_NAMES).not.toContain("code_write");
    expect(TOOL_NAMES).not.toContain("write_handoff");
    expect(isToolName("write_handoff")).toBe(false);
    expect(listToolDefinitions().map((tool) => tool.name)).not.toContain("write_handoff");
    expect(TOOL_NAMES).toContain("finish_work");
    expect(TOOL_NAMES).toContain("get_handoff");
  });

  it("requires idempotency_key on create tools", () => {
    for (const name of IDEMPOTENT_TOOLS) {
      const schema = getToolDefinition(name).inputSchema;
      const required = schema["required"];
      expect(Array.isArray(required) ? required : []).toContain("idempotency_key");
    }
  });

  it("covers code and github catalogs", () => {
    expect(CODE_TOOLS).toEqual([
      "get_tree",
      "search_code",
      "get_file",
      "get_symbol",
      "get_owners",
      "get_related_files",
      "get_changed_scope",
    ]);
    expect(GITHUB_TOOLS).toEqual([
      "github_list_prs",
      "github_list_issues",
      "github_link_issue",
      "github_sync_now",
    ]);
  });

  it("rejects missing required fields", () => {
    expect(parseToolArgs("search_context", {}).ok).toBe(false);
    expect(parseToolArgs("create_task", { title: "x" }).ok).toBe(false);
    expect(parseToolArgs("get_task", { task_id: TASK_ID }).ok).toBe(true);
    expect(parseToolArgs("list_comments", {}).ok).toBe(false);
    expect(parseToolArgs("list_comments", { task_id: TASK_ID, cursor: "c1", limit: 20 }).ok).toBe(
      true,
    );
    expect(parseToolArgs("get_project", { project_id: PROJECT_ID }).ok).toBe(true);
  });

  it("publishes a description for every tool", () => {
    for (const tool of listToolDefinitions()) {
      expect(tool.description.length).toBeGreaterThan(8);
    }
  });
});
