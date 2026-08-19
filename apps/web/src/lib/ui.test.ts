import { describe, expect, it } from "vitest";

import { TASK_STATUSES } from "./roadmap";
import {
  BANNER_TONE_CLASS,
  BUTTON_VARIANT_CLASS,
  FIELD_ERROR_CLASS,
  STATUS_BG_CLASS,
  STATUS_FILL_VAR,
  STATUS_TEXT_CLASS,
  cx,
  segmentedItemClass,
  statusFillVar,
  statusTokenName,
  statusToneClass,
} from "./ui";

describe("ui class maps", () => {
  it("maps every task status onto token class and fill names", () => {
    expect(statusTokenName("in_progress")).toBe("status-in-progress");
    expect(statusToneClass("text", "ready")).toBe("text-status-ready");
    expect(statusToneClass("bg", "in_review")).toBe("bg-status-in-review");
    expect(statusFillVar("blocked")).toBe("var(--status-blocked)");

    for (const status of TASK_STATUSES) {
      expect(STATUS_TEXT_CLASS[status]).toBe(statusToneClass("text", status));
      expect(STATUS_BG_CLASS[status]).toBe(statusToneClass("bg", status));
      expect(STATUS_FILL_VAR[status]).toBe(statusFillVar(status));
    }
  });

  it("keeps button, banner, and field tones on chrome tokens", () => {
    expect(BUTTON_VARIANT_CLASS.primary).toContain("bg-accent");
    expect(BUTTON_VARIANT_CLASS.secondary).toContain("border-border");
    expect(BUTTON_VARIANT_CLASS.danger).toContain("bg-danger");
    expect(BANNER_TONE_CLASS.neutral).toContain("bg-surface");
    expect(BANNER_TONE_CLASS.danger).toContain("text-danger");
    expect(BANNER_TONE_CLASS.warning).toContain("border-warning");
    expect(BANNER_TONE_CLASS.success).toContain("text-success");
    expect(FIELD_ERROR_CLASS).toBe("text-sm text-danger");
  });

  it("joins class parts and skips empty values", () => {
    expect(cx("a", false, undefined, null, "b")).toBe("a b");
    expect(segmentedItemClass(false)).toContain("text-muted");
    expect(segmentedItemClass(true)).toContain("bg-background");
    expect(segmentedItemClass(true)).toContain("text-foreground");
  });
});
