import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  NEW_TASK_HREF,
  buildCommandPaletteItems,
  filterCommandPaletteItems,
  isNewTaskQuery,
} from "./command-palette";
import { t, type MessageKey } from "./i18n";
import { APP_NAV, NEW_PROJECT_PATH } from "./nav";

describe("command palette items", () => {
  it("lists every APP_NAV href including Backlog, then New project", () => {
    const items = buildCommandPaletteItems({ hasProject: false });
    expect(items.map((item) => item.href)).toEqual([
      ...APP_NAV.map((item) => item.href),
      NEW_PROJECT_PATH,
    ]);
    expect(items.some((item) => item.href === "/app/backlog")).toBe(true);
    expect(items.some((item) => item.message === "nav.backlog")).toBe(true);
    expect(items.some((item) => item.message === "command.newProject")).toBe(true);
  });

  it("adds New task only when a project is selected", () => {
    const withoutProject = buildCommandPaletteItems({ hasProject: false });
    const withProject = buildCommandPaletteItems({ hasProject: true });
    expect(withoutProject.some((item) => item.id === "new-task")).toBe(false);
    expect(withoutProject.some((item) => item.href === NEW_TASK_HREF)).toBe(false);
    expect(withProject.at(-1)).toEqual({
      id: "new-task",
      href: NEW_TASK_HREF,
      message: "command.newTask",
    });
  });

  it("does not expose language or theme as commands", () => {
    const items = buildCommandPaletteItems({ hasProject: true });
    const messages = items.map((item) => item.message);
    expect(messages).not.toContain("common.language");
    expect(messages).not.toContain("settings.language");
    expect(messages.every((message) => !message.includes("theme"))).toBe(true);
    expect(messages.every((message) => !message.includes("locale"))).toBe(true);
  });

  it("filters by localized label and leaves an empty list when nothing matches", () => {
    const items = buildCommandPaletteItems({ hasProject: true });
    const labelOf = (key: MessageKey) => t(key, "en");
    expect(filterCommandPaletteItems(items, "back", labelOf).map((item) => item.href)).toEqual([
      "/app/backlog",
    ]);
    expect(filterCommandPaletteItems(items, "new task", labelOf).map((item) => item.id)).toEqual([
      "new-task",
    ]);
    expect(filterCommandPaletteItems(items, "   ", labelOf)).toEqual(items);
    expect(filterCommandPaletteItems(items, "zzzz-no-match", labelOf)).toEqual([]);
  });

  it("treats only new=1 as the create-task query", () => {
    expect(isNewTaskQuery("?new=1")).toBe(true);
    expect(isNewTaskQuery("new=1")).toBe(true);
    expect(isNewTaskQuery("?new=1&x=2")).toBe(true);
    expect(isNewTaskQuery("?new=true")).toBe(false);
    expect(isNewTaskQuery("")).toBe(false);
  });

  it("mounts from AppShell and opens CreateTaskForm from ?new=1", () => {
    const webRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
    const shell = readFileSync(join(webRoot, "app/app/app-shell.tsx"), "utf8");
    const board = readFileSync(join(webRoot, "app/app/board/page.tsx"), "utf8");
    expect(shell).toContain("CommandPalette");
    expect(shell).toContain("hasProject={project !== null}");
    expect(board).toContain("isNewTaskQuery");
    expect(board).toContain('router.replace("/app/board"');
    expect(board).toContain("CreateTaskForm");
    expect(board).toContain("onOpenChange={setCreateOpen}");
  });
});
