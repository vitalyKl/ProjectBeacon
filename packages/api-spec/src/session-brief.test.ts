import { describe, expect, it } from "vitest";

import { ContextSectionSchema, SessionBriefSchema } from "./session-brief.js";

const UUID = "018f1e2c-3d4e-7000-8000-000000000001";
const REPO_ID = "018f1e2c-3d4e-7000-8000-000000000002";
const TASK_ID = "018f1e2c-3d4e-7000-8000-000000000003";
const NODE_ID = "018f1e2c-3d4e-7000-8000-000000000004";
const CONSTRAINT_ID = "018f1e2c-3d4e-7000-8000-000000000005";
const DECISION_ID = "018f1e2c-3d4e-7000-8000-000000000006";
const HANDOFF_ID = "018f1e2c-3d4e-7000-8000-000000000007";
const SESSION_ID = "018f1e2c-3d4e-7000-8000-000000000008";
const MILESTONE_ID = "018f1e2c-3d4e-7000-8000-000000000009";
const REVISION_ID = "018f1e2c-3d4e-7000-8000-00000000000a";

const validSessionBrief = {
  schema_version: "1",
  compiler_version: "1.0.0",
  project: { id: UUID, name: "Beacon", slug: "beacon" },
  compiled_at: "2026-08-17T12:00:00.000Z",
  revision_id: REVISION_ID,
  compiled_hash: "abc123",
  target: { repo_id: REPO_ID, path: "packages/api-spec", task_id: TASK_ID },
  milestone: { id: MILESTONE_ID, title: "v1", status: "open" },
  task: {
    id: TASK_ID,
    title: "Land SessionBrief schemas",
    status: "in_progress",
    type: "task",
    milestone_id: MILESTONE_ID,
    acceptance_md: "Zod schemas parse a valid brief.",
    linked_paths: [{ repo_id: REPO_ID, path: "packages/api-spec/src" }],
  },
  sections: [
    {
      id: "security",
      title: "Security",
      body_md: "Do not leak tokens.",
      ordinal: 0,
    },
    {
      id: "custom",
      key: "dogfood",
      title: "Dogfood",
      body_md: "Use Beacon on Beacon.",
      ordinal: 1,
    },
  ],
  constraints: [
    {
      id: CONSTRAINT_ID,
      kind: "security",
      body: "Do not exfiltrate secrets.",
      scope_path: "",
      status: "active",
    },
  ],
  decisions_relevant: [
    {
      id: DECISION_ID,
      title: "Zod is the contract",
      status: "accepted",
      decision: "Generate OpenAPI from Zod.",
      related_paths: ["packages/api-spec"],
    },
  ],
  handoff: {
    id: HANDOFF_ID,
    session_id: SESSION_ID,
    summary: "Schemas landed; compiler still pending.",
    next_steps: "Implement compileSessionBrief.",
    files_touched: [{ repo_id: REPO_ID, path: "packages/api-spec/src/session-brief.ts" }],
    open_questions: [],
    created_at: "2026-08-17T11:00:00.000Z",
  },
  changed_scope: {
    paths: [{ repo_id: REPO_ID, path: "packages/api-spec" }],
    reasons: [{ path: "packages/api-spec", repo_id: REPO_ID, reason: "linked_path" }],
  },
  tree_capsule: {
    repo_id: REPO_ID,
    root: "",
    entries: [
      { path: "packages", kind: "dir", file_count: 12, langs: { ts: 12 }, important: false },
    ],
  },
  budget: {
    requested: 8000,
    used_estimate: 120,
    tokenizer: "js_length_div_4",
    overflow: false,
    dropped: [],
  },
  sources: [{ node_id: NODE_ID, scope_type: "project", path: "" }],
} as const;

describe("SessionBriefSchema", () => {
  it("parses a valid fixture", () => {
    const parsed = SessionBriefSchema.parse(validSessionBrief);
    expect(parsed.schema_version).toBe("1");
    expect(parsed.budget.tokenizer).toBe("js_length_div_4");
    expect(parsed.task?.status).toBe("in_progress");
  });

  it("rejects missing never-drop required fields", () => {
    expect(SessionBriefSchema.safeParse({ ...validSessionBrief, constraints: undefined }).success).toBe(
      false,
    );
    expect(SessionBriefSchema.safeParse({ ...validSessionBrief, budget: undefined }).success).toBe(
      false,
    );
    expect(SessionBriefSchema.safeParse({ ...validSessionBrief, project: undefined }).success).toBe(
      false,
    );
    expect(
      SessionBriefSchema.safeParse({ ...validSessionBrief, schema_version: undefined }).success,
    ).toBe(false);

    expect(
      SessionBriefSchema.safeParse({
        ...validSessionBrief,
        task: {
          ...validSessionBrief.task,
          acceptance_md: undefined,
        },
      }).success,
    ).toBe(false);

    expect(
      SessionBriefSchema.safeParse({
        ...validSessionBrief,
        sections: [
          {
            id: "non_goals",
            title: "Non-goals",
            ordinal: 0,
          },
        ],
      }).success,
    ).toBe(false);
  });

  it("requires key on custom sections", () => {
    expect(
      ContextSectionSchema.safeParse({
        id: "custom",
        title: "Extra",
        body_md: "needs a key",
        ordinal: 0,
      }).success,
    ).toBe(false);

    expect(
      ContextSectionSchema.safeParse({
        id: "custom",
        key: "extra",
        title: "Extra",
        body_md: "has a key",
        ordinal: 0,
      }).success,
    ).toBe(true);

    expect(
      ContextSectionSchema.safeParse({
        id: "goals",
        title: "Goals",
        body_md: "Ship it",
        ordinal: 0,
      }).success,
    ).toBe(true);
  });
});
