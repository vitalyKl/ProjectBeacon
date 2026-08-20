import { describe, expect, it } from "vitest";

import {
  isTaskDetailFromBacklog,
  TASK_DETAIL_BACKLOG_HREF,
  TASK_DETAIL_BOARD_HREF,
  taskDetailCrumbs,
  taskDetailFallbackHref,
  taskDetailHref,
} from "./task-detail";

describe("taskDetailFallbackHref", () => {
  it("always returns /app/board and never consults a referrer", () => {
    expect(taskDetailFallbackHref()).toBe("/app/board");
    expect(taskDetailFallbackHref()).toBe(TASK_DETAIL_BOARD_HREF);
  });
});

describe("isTaskDetailFromBacklog", () => {
  it("is true only for from=backlog", () => {
    expect(isTaskDetailFromBacklog("from=backlog")).toBe(true);
    expect(isTaskDetailFromBacklog("?from=backlog")).toBe(true);
    expect(isTaskDetailFromBacklog(new URLSearchParams("from=backlog"))).toBe(true);
    expect(isTaskDetailFromBacklog("from=board")).toBe(false);
    expect(isTaskDetailFromBacklog("")).toBe(false);
    expect(isTaskDetailFromBacklog(null)).toBe(false);
  });
});

describe("taskDetailHref", () => {
  it("adds from=backlog only when opened from Backlog", () => {
    expect(taskDetailHref("task-1")).toBe("/app/tasks/task-1");
    expect(taskDetailHref("task-1", "board")).toBe("/app/tasks/task-1");
    expect(taskDetailHref("task-1", "backlog")).toBe("/app/tasks/task-1?from=backlog");
  });
});

describe("taskDetailCrumbs", () => {
  it("is Board plus title, with optional Backlog and no Work crumb", () => {
    expect(taskDetailCrumbs("Ship two columns")).toEqual([
      { type: "link", href: TASK_DETAIL_BOARD_HREF, surface: "board" },
      { type: "title", text: "Ship two columns" },
    ]);
    expect(taskDetailCrumbs("Ship two columns", { fromBacklog: true })).toEqual([
      { type: "link", href: TASK_DETAIL_BOARD_HREF, surface: "board" },
      { type: "link", href: TASK_DETAIL_BACKLOG_HREF, surface: "backlog" },
      { type: "title", text: "Ship two columns" },
    ]);
    const surfaces = taskDetailCrumbs("Ship two columns", { fromBacklog: true }).flatMap((crumb) =>
      crumb.type === "link" ? [crumb.surface] : [],
    );
    expect(surfaces).not.toContain("work");
    expect(taskDetailCrumbs("Ship two columns").some((crumb) => crumb.type === "title")).toBe(true);
  });
});
