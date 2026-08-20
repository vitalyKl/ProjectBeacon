import type { MessageKey } from "./i18n";
import { APP_NAV, NEW_PROJECT_PATH } from "./nav";

export const NEW_TASK_HREF = "/app/board?new=1";

export type CommandPaletteItem = {
  id: string;
  href: string;
  message: MessageKey;
};

export function buildCommandPaletteItems(options: { hasProject: boolean }): CommandPaletteItem[] {
  const items: CommandPaletteItem[] = APP_NAV.map((item) => ({
    id: item.href,
    href: item.href,
    message: item.message,
  }));
  items.push({
    id: "new-project",
    href: NEW_PROJECT_PATH,
    message: "command.newProject",
  });
  if (options.hasProject) {
    items.push({
      id: "new-task",
      href: NEW_TASK_HREF,
      message: "command.newTask",
    });
  }
  return items;
}

export function filterCommandPaletteItems(
  items: CommandPaletteItem[],
  query: string,
  labelOf: (message: MessageKey) => string,
): CommandPaletteItem[] {
  const needle = query.trim().toLowerCase();
  if (!needle) {
    return items;
  }
  return items.filter((item) => labelOf(item.message).toLowerCase().includes(needle));
}

export function isNewTaskQuery(search: string): boolean {
  const params = new URLSearchParams(search.startsWith("?") ? search.slice(1) : search);
  return params.get("new") === "1";
}
