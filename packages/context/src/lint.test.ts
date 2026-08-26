import { describe, expect, it } from "vitest";

import type { ConstraintView, DecisionSummary } from "@beacon/api-spec";

import { lintContradictingConstraints, lintMilestoneOrphans, lintSupersedeLinks } from "./lint.js";

describe("lintSupersedeLinks", () => {
  function fakeDecision(
    overrides: Partial<DecisionSummary> = {},
  ): DecisionSummary {
    return {
      id: "00000000-0000-0000-0000-000000000001",
      title: "Test decision",
      status: "accepted",
      decision: "dec",
      related_paths: [],
      superseded_by: null,
      ...overrides,
    };
  }

  it("returns no warnings when all decisions are accepted", () => {
    const decisions = [fakeDecision(), fakeDecision()];
    expect(lintSupersedeLinks(decisions)).toEqual([]);
  });

  it("flags a superseded decision whose superseder is missing", () => {
    const existing = fakeDecision();
    const superseded = fakeDecision({
      id: "00000000-0000-0000-0000-00000000000a",
      title: "Old decision",
      status: "superseded",
      superseded_by: "00000000-0000-0000-0000-00000000ffff",
    });

    const warnings = lintSupersedeLinks([existing, superseded]);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]!.code).toBe("unlinked_supersede");
  });

  it("does not flag a valid supersede chain", () => {
    const supersedes = fakeDecision({
      id: "00000000-0000-0000-0000-00000000000b",
      title: "New decision",
      status: "accepted",
    });
    const superseded = fakeDecision({
      id: "00000000-0000-0000-0000-00000000000a",
      title: "Old decision",
      status: "superseded",
      superseded_by: "00000000-0000-0000-0000-00000000000b",
    });

    const warnings = lintSupersedeLinks([supersedes, superseded]);
    expect(warnings).toEqual([]);
  });
});

describe("lintMilestoneOrphans", () => {
  it("returns empty when no milestone is set", () => {
    expect(lintMilestoneOrphans(null, [])).toEqual([]);
  });

  it("returns empty when no tasks reference the milestone", () => {
    expect(lintMilestoneOrphans("ms-1", [])).toEqual([]);
  });

  it("returns empty when tasks are still in progress", () => {
    const tasks = [
      { id: "t1", milestoneId: "ms-1", status: "ready", deletedAt: null },
    ];
    expect(lintMilestoneOrphans("ms-1", tasks)).toEqual([]);
  });

  it("warns when completed tasks are still linked to the milestone", () => {
    const tasks = [
      { id: "t1", milestoneId: "ms-1", status: "done", deletedAt: null },
      { id: "t2", milestoneId: "ms-1", status: "canceled", deletedAt: null },
    ];
    const warnings = lintMilestoneOrphans("ms-1", tasks);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]!.code).toBe("milestone_orphaned_tasks");
    expect(warnings[0]!.detail).toContain("2");
  });

  it("excludes deleted tasks from the count", () => {
    const tasks = [
      { id: "t1", milestoneId: "ms-1", status: "done", deletedAt: "2026-01-01T00:00:00Z" },
    ];
    expect(lintMilestoneOrphans("ms-1", tasks)).toEqual([]);
  });
});

describe("lintContradictingConstraints", () => {
  function fakeConstraint(overrides: Partial<ConstraintView> = {}): ConstraintView {
    return {
      id: "00000000-0000-0000-0000-000000000001",
      kind: "must",
      body: "must use TypeScript",
      scope_path: "",
      status: "active",
      ...overrides,
    } satisfies ConstraintView;
  }

  it("returns empty for a single constraint", () => {
    expect(lintContradictingConstraints([fakeConstraint()])).toEqual([]);
  });

  it("returns empty for two non-contradicting constraints", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000010", body: "must use TypeScript" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000011", body: "should use ESLint" }),
    ];
    expect(lintContradictingConstraints(constraints)).toEqual([]);
  });

  it("detects a direct contradiction: must use vs must not use", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000020", body: "must use TypeScript" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000021", kind: "must_not", body: "must not use TypeScript" }),
    ];
    const warnings = lintContradictingConstraints(constraints);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]!.code).toBe("constraint_contradiction");
  });

  it("detects a contradiction with should/should not", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000030", body: "should use React" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000031", body: "should not use React" }),
    ];
    const warnings = lintContradictingConstraints(constraints);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]!.code).toBe("constraint_contradiction");
  });

  it("does not flag constraints in different scopes", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000040", body: "must use TypeScript", scope_path: "apps/api/" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000041", body: "must not use TypeScript", scope_path: "apps/web/" }),
    ];
    expect(lintContradictingConstraints(constraints)).toEqual([]);
  });

  it("flags multiple contradicting pairs independently", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000050", body: "must use TypeScript" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000051", body: "must not use TypeScript" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000052", body: "should use React" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000053", body: "should not use React" }),
    ];
    const warnings = lintContradictingConstraints(constraints);
    expect(warnings).toHaveLength(2);
  });

  it("does not flag constraints with different subjects", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000060", body: "must use TypeScript" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000061", body: "must not use Rust" }),
    ];
    expect(lintContradictingConstraints(constraints)).toEqual([]);
  });

  it("flags constraints with shared but non-keyword words", () => {
    const constraints: ConstraintView[] = [
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000070", body: "must use the old system" }),
      fakeConstraint({ id: "00000000-0000-0000-0000-000000000071", body: "must not use the new system" }),
    ];
    const warnings = lintContradictingConstraints(constraints);
    expect(warnings).toHaveLength(1);
  });
});
