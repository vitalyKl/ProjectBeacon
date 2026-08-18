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
  "add_comment",
  "set_status",
  "link_dependency",
  "list_decisions",
  "record_decision",
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
  "write_handoff",
  "get_handoff",
  "github_list_prs",
  "github_list_issues",
  "github_link_issue",
  "github_sync_now",
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

  it("does not expose run_shell or code:write", () => {
    expect(TOOL_NAMES).not.toContain("run_shell");
    expect(TOOL_NAMES).not.toContain("code:write");
    expect(TOOL_NAMES).not.toContain("code_write");
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
    expect(parseToolArgs("write_handoff", { summary: "handoff without a target id" }).ok).toBe(
      false,
    );
    expect(
      parseToolArgs("write_handoff", {
        summary: "enough characters for a handoff",
        task_id: TASK_ID,
      }).ok,
    ).toBe(true);
    expect(parseToolArgs("get_task", { task_id: TASK_ID }).ok).toBe(true);
    expect(parseToolArgs("get_project", { project_id: PROJECT_ID }).ok).toBe(true);
  });

  it("publishes write_handoff session_id or task_id in JSON Schema", () => {
    const schema = getToolDefinition("write_handoff").inputSchema;
    const variants = [schema["anyOf"], schema["oneOf"]].find(Array.isArray) as
      Record<string, unknown>[] | undefined;
    expect(variants).toBeDefined();
    const requiredSets = (variants ?? []).map((variant) => {
      const required = variant["required"];
      return Array.isArray(required) ? required : [];
    });
    expect(requiredSets.some((required) => required.includes("session_id"))).toBe(true);
    expect(requiredSets.some((required) => required.includes("task_id"))).toBe(true);
  });

  it("publishes a description for every tool", () => {
    for (const tool of listToolDefinitions()) {
      expect(tool.description.length).toBeGreaterThan(8);
    }
  });
});
