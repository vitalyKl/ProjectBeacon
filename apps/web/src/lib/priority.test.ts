import { describe, expect, it } from "vitest";

import {
  compareTaskPriority,
  DEFAULT_TASK_PRIORITY,
  namedPriorityValue,
  priorityLabel,
  prioritySelectOptions,
  sortTasksByPriority,
} from "./priority";

describe("task priority", () => {
  it("keeps default 0 as Normal and leaves elevated integers above named levels", () => {
    expect(DEFAULT_TASK_PRIORITY).toBe(0);
    expect(priorityLabel(2)).toBe("Urgent");
    expect(priorityLabel(1)).toBe("High");
    expect(priorityLabel(0)).toBe("Normal");
    expect(priorityLabel(-1)).toBe("Low");
    expect(priorityLabel(10)).toBe("P10");
    expect(namedPriorityValue(5)).toBeNull();
  });

  it("sorts higher integers first, then recency", () => {
    const urgent = { id: "urgent", priority: 2, updated_at: "2026-08-18T12:00:00.000Z" };
    const existingHigh = { id: "existing", priority: 10, updated_at: "2026-08-18T11:00:00.000Z" };
    const laterNormal = { id: "later", priority: 0, updated_at: "2026-08-18T14:00:00.000Z" };
    const earlierNormal = { id: "earlier", priority: 0, updated_at: "2026-08-18T13:00:00.000Z" };
    const low = { id: "low", priority: -1, updated_at: "2026-08-18T15:00:00.000Z" };

    expect(
      sortTasksByPriority([low, earlierNormal, laterNormal, urgent, existingHigh]).map(
        (item) => item.id,
      ),
    ).toEqual(["existing", "urgent", "later", "earlier", "low"]);
    expect(compareTaskPriority(urgent, existingHigh)).toBeGreaterThan(0);
  });

  it("keeps a custom stored value in the select so 5 and 10 are not coerced", () => {
    expect(prioritySelectOptions(0).map((item) => item.value)).toEqual([2, 1, 0, -1]);
    expect(prioritySelectOptions(10)).toEqual([
      { value: 10, label: "P10" },
      { value: 2, label: "Urgent" },
      { value: 1, label: "High" },
      { value: 0, label: "Normal" },
      { value: -1, label: "Low" },
    ]);
  });
});
