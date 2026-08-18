import { describe, expect, it } from "vitest";

import { invoke } from "./invoke.js";
import { IDEMPOTENT_TOOLS, TOOL_NAMES, type ToolName } from "./tools.js";
import type { InvokeContext } from "./types.js";

const PROJECT_ID = "01934567-89ab-7cde-89ab-0123456789ac";
const TASK_ID = "01934567-89ab-7cde-89ab-0123456789ab";
const SESSION_ID = "01934567-89ab-7cde-89ab-0123456789ad";
const CONSTRAINT_ID = "01934567-89ab-7cde-89ab-0123456789ae";
const REPO_ID = "01934567-89ab-7cde-89ab-0123456789af";
const FROM_TASK_ID = "01934567-89ab-7cde-89ab-0123456789b0";
const TO_TASK_ID = "01934567-89ab-7cde-89ab-0123456789b1";

type RecordedCall = {
  method: string;
  url: URL;
  headers: Record<string, string>;
  body: unknown;
};

function headerMap(headers: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (!headers) {
    return out;
  }
  if (headers instanceof Headers) {
    headers.forEach((value, key) => {
      out[key.toLowerCase()] = value;
    });
    return out;
  }
  if (Array.isArray(headers)) {
    for (const entry of headers) {
      const [key, value] = entry as [string, string];
      out[key.toLowerCase()] = value;
    }
    return out;
  }
  for (const [key, value] of Object.entries(headers as Record<string, string>)) {
    out[key.toLowerCase()] = value;
  }
  return out;
}

function mockFetch(
  handler: (call: RecordedCall) => { status?: number; body?: unknown } | Response,
) {
  const calls: RecordedCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const url = new URL(
      typeof input === "string" ? input : input instanceof URL ? input.href : input.url,
    );
    const call: RecordedCall = {
      method: (init?.method ?? "GET").toUpperCase(),
      url,
      headers: headerMap(init?.headers),
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
    };
    calls.push(call);
    const result = handler(call);
    if (result instanceof Response) {
      return result;
    }
    return new Response(JSON.stringify(result.body ?? { ok: true }), {
      status: result.status ?? 200,
      headers: { "content-type": "application/json" },
    });
  };
  return { fetchImpl, calls };
}

function ctx(fetchImpl: typeof fetch, extra: Partial<InvokeContext> = {}): InvokeContext {
  return {
    baseUrl: "https://beacon.test",
    token: "tok_test",
    projectId: PROJECT_ID,
    fetch: fetchImpl,
    ...extra,
  };
}

function expectRoute(call: RecordedCall | undefined, method: string, path: string) {
  expect(call).toBeDefined();
  expect(call?.method).toBe(method);
  expect(call?.url.pathname).toBe(path);
}

describe("invoke routing", () => {
  it("maps every non-code, non-github tool to the matching /v1 route", async () => {
    const { fetchImpl, calls } = mockFetch((call) => {
      if (call.method === "GET" && call.url.pathname === `/v1/tasks/${TASK_ID}`) {
        return { body: { id: TASK_ID, project_id: PROJECT_ID } };
      }
      if (call.method === "GET" && call.url.pathname === `/v1/tasks/${TASK_ID}/handoff`) {
        return { body: { session_id: SESSION_ID, summary: "previous" } };
      }
      return { body: { ok: true } };
    });

    const args: Record<string, unknown> = {
      get_project: {},
      get_context_pack: { path: "src", budget_tokens: 2000 },
      search_context: { q: "auth" },
      get_task_brief: { task_id: TASK_ID, path: "apps/api" },
      list_milestones: { include_closed: true },
      list_tasks: { status: ["ready", "in_progress"], q: "lock", cursor: "c1", limit: 20 },
      get_task: { task_id: TASK_ID },
      create_task: { title: "Ship MCP", idempotency_key: "create-1" },
      update_task: { task_id: TASK_ID, expected_version: 2, title: "Renamed" },
      add_comment: { task_id: TASK_ID, body: "note", idempotency_key: "comment-1" },
      set_status: { task_id: TASK_ID, status: "ready", expected_version: 3 },
      link_dependency: { from_task_id: FROM_TASK_ID, to_task_id: TO_TASK_ID, type: "blocks" },
      list_decisions: { q: "auth", cursor: "d1" },
      record_decision: {
        title: "Use bearer",
        context: "MCP needs a token",
        decision: "Pass Authorization",
        idempotency_key: "adr-1",
      },
      get_constraints: { path: "apps/api", active_only: true },
      create_constraint: { kind: "must", body: "No SQL in mcp-tools", idempotency_key: "c-1" },
      apply_constraint: { constraint_id: CONSTRAINT_ID },
      start_work: { task_id: TASK_ID, idempotency_key: "start-1", steal: true },
      finish_work: { session_id: SESSION_ID, summary: "Finished the client mapping work" },
      get_handoff: { task_id: TASK_ID },
    };

    const expected: Record<string, [string, string]> = {
      get_project: ["GET", `/v1/projects/${PROJECT_ID}`],
      get_context_pack: ["POST", `/v1/projects/${PROJECT_ID}/context/compile`],
      search_context: ["GET", `/v1/projects/${PROJECT_ID}/context/search`],
      get_task_brief: ["POST", `/v1/projects/${PROJECT_ID}/context/compile`],
      list_milestones: ["GET", `/v1/projects/${PROJECT_ID}/milestones`],
      list_tasks: ["GET", `/v1/projects/${PROJECT_ID}/tasks`],
      get_task: ["GET", `/v1/tasks/${TASK_ID}`],
      create_task: ["POST", `/v1/projects/${PROJECT_ID}/tasks`],
      update_task: ["PATCH", `/v1/tasks/${TASK_ID}`],
      add_comment: ["POST", `/v1/tasks/${TASK_ID}/comments`],
      set_status: ["POST", `/v1/tasks/${TASK_ID}/status`],
      link_dependency: ["POST", `/v1/tasks/${FROM_TASK_ID}/dependencies`],
      list_decisions: ["GET", `/v1/projects/${PROJECT_ID}/decisions`],
      record_decision: ["POST", `/v1/projects/${PROJECT_ID}/decisions`],
      get_constraints: ["GET", `/v1/projects/${PROJECT_ID}/constraints`],
      create_constraint: ["POST", `/v1/projects/${PROJECT_ID}/constraints`],
      apply_constraint: ["POST", `/v1/constraints/${CONSTRAINT_ID}/apply`],
      start_work: ["POST", `/v1/projects/${PROJECT_ID}/sessions`],
      finish_work: ["POST", `/v1/sessions/${SESSION_ID}/finish`],
      get_handoff: ["GET", `/v1/tasks/${TASK_ID}/handoff`],
    };

    for (const [tool, toolArgs] of Object.entries(args)) {
      calls.length = 0;
      await invoke(tool, toolArgs, ctx(fetchImpl));
      const target = expected[tool];
      expect(target, tool).toBeDefined();
      const [method, path] = target ?? ["", ""];
      const match = calls.find((call) => call.url.pathname === path && call.method === method);
      expect(match, tool).toBeDefined();
      expectRoute(match, method, path);
    }
  });

  it("sends Idempotency-Key on required tools and omits the field from JSON", async () => {
    const { fetchImpl, calls } = mockFetch((call) => {
      if (call.method === "GET" && call.url.pathname === `/v1/tasks/${TASK_ID}`) {
        return { body: { id: TASK_ID, project_id: PROJECT_ID } };
      }
      return { body: { ok: true } };
    });

    const payloads: Record<(typeof IDEMPOTENT_TOOLS)[number], unknown> = {
      create_task: { title: "New", idempotency_key: "idemp-create" },
      add_comment: { task_id: TASK_ID, body: "hi", idempotency_key: "idemp-comment" },
      record_decision: {
        title: "ADR",
        context: "why",
        decision: "what",
        idempotency_key: "idemp-adr",
      },
      create_constraint: { kind: "must_not", body: "No SQL", idempotency_key: "idemp-rule" },
      start_work: { task_id: TASK_ID, idempotency_key: "idemp-start" },
    };

    for (const tool of IDEMPOTENT_TOOLS) {
      calls.length = 0;
      await invoke(tool, payloads[tool], ctx(fetchImpl));
      const write = calls.find((call) => call.method === "POST" && call.headers["idempotency-key"]);
      expect(write, tool).toBeDefined();
      expect(write?.headers["authorization"]).toBe("Bearer tok_test");
      expect(write?.headers["idempotency-key"]).toMatch(/^idemp-/);
      expect(write?.body).toEqual(
        expect.not.objectContaining({ idempotency_key: expect.anything() }),
      );
    }
  });

  it("forwards query filters used by list tools", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { items: [], next_cursor: null } }));
    await invoke(
      "list_tasks",
      {
        milestone_id: TASK_ID,
        status: ["ready"],
        q: "lock",
        assignee: "ada",
        cursor: "abc",
        limit: 10,
      },
      ctx(fetchImpl),
    );
    expect(calls[0]?.url.searchParams.get("status")).toBe("ready");
    expect(calls[0]?.url.searchParams.get("q")).toBe("lock");
    expect(calls[0]?.url.searchParams.get("assignee")).toBe("ada");
    expect(calls[0]?.url.searchParams.get("cursor")).toBe("abc");
    expect(calls[0]?.url.searchParams.get("limit")).toBe("10");
  });

  it("maps HTTP error JSON to ToolError", async () => {
    const { fetchImpl } = mockFetch(() => ({
      status: 409,
      body: { error: { code: "task_locked", message: "locked", details: { session: "s1" } } },
    }));
    await expect(invoke("get_task", { task_id: TASK_ID }, ctx(fetchImpl))).rejects.toMatchObject({
      name: "ToolError",
      code: "task_locked",
      status: 409,
      details: { session: "s1" },
    });
  });

  it("surfaces 503 code_index_unavailable when code HTTP is missing", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({
      status: 503,
      body: { error: { code: "code_index_unavailable", message: "no index" } },
    }));

    await expect(invoke("get_tree", { repo_id: REPO_ID }, ctx(fetchImpl))).rejects.toMatchObject({
      code: "code_index_unavailable",
      status: 503,
    });
    expectRoute(calls[0], "GET", `/v1/repos/${REPO_ID}/tree`);

    const missing = mockFetch(() => ({
      status: 404,
      body: { error: { code: "not_found", message: "nope" } },
    }));
    await expect(
      invoke("search_code", { q: "compile", repo_id: REPO_ID }, ctx(missing.fetchImpl)),
    ).rejects.toMatchObject({ code: "code_index_unavailable", status: 503 });
  });

  it("uses defaultRepoId and rejects omitted repo_id when ambiguous", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({
      status: 503,
      body: { error: { code: "code_index_unavailable", message: "no index" } },
    }));
    await expect(
      invoke("get_file", { path: "src/index.ts" }, ctx(fetchImpl, { defaultRepoId: REPO_ID })),
    ).rejects.toMatchObject({ code: "code_index_unavailable" });
    expect(calls[0]?.url.pathname).toBe(`/v1/repos/${REPO_ID}/files`);

    await expect(
      invoke("get_owners", { path: "src/index.ts" }, ctx(fetchImpl)),
    ).rejects.toMatchObject({
      code: "repo_ambiguous",
      status: 400,
    });
  });

  it("lets a custom CodeSource short-circuit HTTP", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    const result = await invoke(
      "get_symbol",
      { name: "invoke", repo_id: REPO_ID },
      {
        ...ctx(fetchImpl),
        codeSource: {
          getTree: async () => ({ entries: [] }),
          searchCode: async () => ({ items: [] }),
          getFile: async () => ({ body: "" }),
          getSymbol: async () => ({ name: "invoke" }),
          getOwners: async () => ({ owners: [] }),
          getRelatedFiles: async () => ({ paths: [] }),
          getChangedScope: async () => ({ paths: [] }),
        },
      },
    );
    expect(result).toEqual({ name: "invoke" });
    expect(calls).toHaveLength(0);
  });

  it("fails write_handoff closed without finishing a session", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { unexpected: true } }));
    await expect(
      invoke(
        "write_handoff",
        { session_id: SESSION_ID, summary: "Leaving a handoff without status" },
        ctx(fetchImpl),
      ),
    ).rejects.toMatchObject({
      code: "not_found",
      status: 501,
      details: { reason: "handoff_route_unavailable" },
    });
    await expect(
      invoke(
        "write_handoff",
        { task_id: TASK_ID, summary: "Leaving a handoff without status" },
        ctx(fetchImpl),
      ),
    ).rejects.toMatchObject({ status: 501, details: { reason: "handoff_route_unavailable" } });
    expect(calls).toHaveLength(0);
  });

  it("maps github_* tools to /v1 and requires repo_id when default is missing", async () => {
    const { fetchImpl, calls } = mockFetch(() => ({ body: { items: [] } }));
    await invoke(
      "github_list_issues",
      { repo_id: REPO_ID, state: "open", q: "lock" },
      ctx(fetchImpl),
    );
    expectRoute(calls[0], "GET", `/v1/repos/${REPO_ID}/github/issues`);
    expect(calls[0]?.url.searchParams.get("state")).toBe("open");
    expect(calls[0]?.url.searchParams.get("q")).toBe("lock");

    await invoke("github_list_prs", { state: "open" }, ctx(fetchImpl, { defaultRepoId: REPO_ID }));
    expectRoute(calls[1], "GET", `/v1/repos/${REPO_ID}/github/pulls`);

    await invoke(
      "github_list_prs",
      { state: "open", task_id: TASK_ID },
      ctx(fetchImpl, { defaultRepoId: REPO_ID }),
    );
    expectRoute(calls[2], "GET", `/v1/repos/${REPO_ID}/github/pulls`);
    expect(calls[2]?.url.searchParams.get("task_id")).toBe(TASK_ID);

    await invoke(
      "github_link_issue",
      { task_id: TASK_ID, issue_number: 12, repo_id: REPO_ID },
      ctx(fetchImpl),
    );
    expectRoute(calls[3], "POST", `/v1/tasks/${TASK_ID}/github-issue`);
    expect(calls[3]?.body).toEqual({ issue_number: 12, repo_id: REPO_ID });

    await invoke("github_sync_now", {}, ctx(fetchImpl, { defaultRepoId: REPO_ID }));
    expectRoute(calls[4], "POST", `/v1/repos/${REPO_ID}/github/sync`);

    await expect(invoke("github_list_issues", {}, ctx(fetchImpl))).rejects.toMatchObject({
      code: "repo_ambiguous",
      status: 400,
    });
  });

  it("does not invent run_shell", () => {
    expect(TOOL_NAMES.includes("run_shell" as ToolName)).toBe(false);
  });
});
