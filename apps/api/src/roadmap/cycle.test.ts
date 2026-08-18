import { describe, expect, it } from "vitest";

import { wouldCreateCycle } from "./cycle.js";

describe("wouldCreateCycle", () => {
  it("detects a direct cycle", () => {
    expect(wouldCreateCycle([{ fromTaskId: "b", toTaskId: "a" }], "a", "b")).toBe(true);
  });

  it("detects a transitive cycle", () => {
    const edges = [
      { fromTaskId: "b", toTaskId: "c" },
      { fromTaskId: "c", toTaskId: "a" },
    ];
    expect(wouldCreateCycle(edges, "a", "b")).toBe(true);
  });

  it("allows a DAG edge", () => {
    const edges = [
      { fromTaskId: "a", toTaskId: "b" },
      { fromTaskId: "b", toTaskId: "c" },
    ];
    expect(wouldCreateCycle(edges, "a", "c")).toBe(false);
  });

  it("treats a self-edge as a cycle", () => {
    expect(wouldCreateCycle([], "a", "a")).toBe(true);
  });
});
