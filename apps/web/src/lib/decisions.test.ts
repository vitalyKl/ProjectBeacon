import { afterEach, describe, expect, it, vi } from "vitest";

import {
  applyConstraint,
  canAcceptDecision,
  canApplyConstraint,
  canDeprecateDecision,
  canSupersedeDecision,
  constraintKindLabel,
  constraintStatusLabel,
  createConstraint,
  createDecision,
  decisionStatusLabel,
  fetchProjectConstraints,
  fetchProjectDecisions,
  patchDecision,
  sortConstraints,
  sortDecisions,
  successorDecisionOptions,
  type PublicConstraint,
  type PublicDecision,
} from "./decisions";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function decision(overrides: Partial<PublicDecision> = {}): PublicDecision {
  return {
    id: "dec-1",
    project_id: "proj-1",
    title: "Use Zod",
    status: "accepted",
    context: "Need a contract",
    decision: "Generate OpenAPI from Zod.",
    consequences: "",
    created_by_type: "user",
    created_by_id: "user-1",
    superseded_by: null,
    created_at: "2026-08-18T12:00:00.000Z",
    related_paths: [],
    related_task_ids: [],
    ...overrides,
  };
}

function constraint(overrides: Partial<PublicConstraint> = {}): PublicConstraint {
  return {
    id: "con-1",
    project_id: "proj-1",
    kind: "must",
    body: "Keep ADRs short",
    scope_path: "",
    status: "proposed",
    created_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

describe("decision labels", () => {
  it("uses the stored status and kind words", () => {
    expect(decisionStatusLabel("proposed")).toBe("proposed");
    expect(constraintKindLabel("must_not")).toBe("must not");
    expect(constraintStatusLabel("active")).toBe("active");
  });
});

describe("sortDecisions", () => {
  it("puts proposed first, then newest within a status", () => {
    const olderAccepted = decision({
      id: "old-accepted",
      status: "accepted",
      created_at: "2026-08-01T00:00:00.000Z",
    });
    const newerAccepted = decision({
      id: "new-accepted",
      status: "accepted",
      created_at: "2026-08-10T00:00:00.000Z",
    });
    const proposed = decision({
      id: "proposed",
      status: "proposed",
      created_at: "2026-08-02T00:00:00.000Z",
    });

    expect(sortDecisions([olderAccepted, newerAccepted, proposed]).map((item) => item.id)).toEqual([
      "proposed",
      "new-accepted",
      "old-accepted",
    ]);
  });
});

describe("sortConstraints", () => {
  it("puts proposed first so apply is visible", () => {
    const active = constraint({
      id: "active",
      status: "active",
      created_at: "2026-08-10T00:00:00.000Z",
    });
    const proposed = constraint({
      id: "proposed",
      status: "proposed",
      created_at: "2026-08-01T00:00:00.000Z",
    });
    const rejected = constraint({
      id: "rejected",
      status: "rejected",
      created_at: "2026-08-11T00:00:00.000Z",
    });

    expect(sortConstraints([active, rejected, proposed]).map((item) => item.id)).toEqual([
      "proposed",
      "active",
      "rejected",
    ]);
  });
});

describe("decision lifecycle helpers", () => {
  it("accepts proposed only and supersedes live decisions", () => {
    expect(canAcceptDecision(decision({ status: "proposed" }))).toBe(true);
    expect(canAcceptDecision(decision({ status: "accepted" }))).toBe(false);
    expect(canSupersedeDecision(decision({ status: "accepted" }))).toBe(true);
    expect(canDeprecateDecision(decision({ status: "superseded" }))).toBe(false);
    expect(
      successorDecisionOptions(
        [
          decision({ id: "current", status: "accepted" }),
          decision({ id: "next", status: "accepted" }),
          decision({ id: "old", status: "deprecated" }),
        ],
        "current",
      ).map((item) => item.id),
    ).toEqual(["next"]);
  });
});

describe("canApplyConstraint", () => {
  it("is only true for proposed constraints", () => {
    expect(canApplyConstraint(constraint({ status: "proposed" }))).toBe(true);
    expect(canApplyConstraint(constraint({ status: "active" }))).toBe(false);
    expect(canApplyConstraint(constraint({ status: "rejected" }))).toBe(false);
  });
});

describe("decisions API client", () => {
  it("lists decisions and constraints with pagination", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [decision({ id: "a" })], next_cursor: "page-2" }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [decision({ id: "b" })], next_cursor: null }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [constraint({ id: "c" })], next_cursor: null }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(fetchProjectDecisions("proj-1")).resolves.toEqual([
      expect.objectContaining({ id: "a" }),
      expect.objectContaining({ id: "b" }),
    ]);
    await expect(fetchProjectConstraints("proj-1")).resolves.toEqual([
      expect.objectContaining({ id: "c" }),
    ]);
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/projects/proj-1/decisions?limit=100",
      expect.objectContaining({ credentials: "same-origin" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/v1/projects/proj-1/constraints?limit=100",
      expect.objectContaining({ credentials: "same-origin" }),
    );
  });

  it("sends Idempotency-Key on create and posts apply", async () => {
    const createdDecision = decision({ id: "created-dec" });
    const createdConstraint = constraint({ id: "created-con" });
    const applied = constraint({ id: "created-con", status: "active" });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify(createdDecision), {
          status: 201,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(createdConstraint), {
          status: 201,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(applied), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(
      createDecision(
        "proj-1",
        { title: "Use Zod", context: "Need a contract", decision: "Generate OpenAPI from Zod." },
        "adr-1",
      ),
    ).resolves.toEqual(createdDecision);
    await expect(
      createConstraint("proj-1", { kind: "must", body: "Keep ADRs short" }, "rule-1"),
    ).resolves.toEqual(createdConstraint);
    await expect(applyConstraint("created-con")).resolves.toEqual(applied);

    expect(fetchMock.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({
        method: "POST",
        headers: expect.any(Headers),
      }),
    );
    const decisionHeaders = fetchMock.mock.calls[0]?.[1]?.headers as Headers;
    expect(decisionHeaders.get("idempotency-key")).toBe("adr-1");
    const constraintHeaders = fetchMock.mock.calls[1]?.[1]?.headers as Headers;
    expect(constraintHeaders.get("idempotency-key")).toBe("rule-1");
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/v1/constraints/created-con/apply",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it("patches decision status", async () => {
    const accepted = decision({ id: "dec-1", status: "accepted" });
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response(JSON.stringify(accepted), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(patchDecision("dec-1", { status: "accepted" })).resolves.toEqual(accepted);
    expect(fetchMock).toHaveBeenCalledWith(
      "/v1/decisions/dec-1",
      expect.objectContaining({ method: "PATCH" }),
    );
  });
});
