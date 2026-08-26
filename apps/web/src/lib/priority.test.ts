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
  it("keeps default 5 as High and leaves elevated integers above named levels", () => {
    expect(DEFAULT_TASK_PRIORITY).toBe(5);
    expect(priorityLabel(4)).toBe("Urgent");
    expect(priorityLabel(5)).toBe("High");
    expect(priorityLabel(6)).toBe("Normal");
    expect(priorityLabel(7)).toBe("Low");
    expect(priorityLabel(10)).toBe("P10");
    expect(namedPriorityValue(3)).toBeNull();
  });

  it("sorts higher integers first, then recency", () => {
    const urgent = { id: "urgent", priority: 4, updated_at: "2026-08-18T12:00:00.000Z" };
    const existingHigh = { id: "existing", priority: 5, updated_at: "2026-08-18T11:00:00.000Z" };
    const laterNormal = { id: "later", priority: 6, updated_at: "2026-08-18T14:00:00.000Z" };
    const earlierNormal = { id: "earlier", priority: 6, updated_at: "2026-08-18T13:00:00.000Z" };
    const low = { id: "low", priority: 7, updated_at: "2026-08-18T15:00:00.000Z" };

    expect(
      sortTasksByPriority([low, earlierNormal, laterNormal, urgent, existingHigh]).map(
        (item) => item.id,
      ),
    ).toEqual(["urgent", "existing", "later", "earlier", "low"]);
    expect(compareTaskPriority(urgent, existingHigh)).toBeLessThan(0);
  });

  it("keeps a custom stored value in the select so 3 and 10 are not coerced", () => {
    expect(prioritySelectOptions(10).map((item) => item.value)).toEqual([10, 4, 5, 6, 7, 8]);
    expect(prioritySelectOptions(3).map((item) => item.value)).toEqual([3, 4, 5, 6, 7, 8]);
  });
});
