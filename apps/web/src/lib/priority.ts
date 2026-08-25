import { t, tf, type MessageKey } from "./i18n";

export const TASK_PRIORITY_LEVELS = [
  { id: "urgent", labelKey: "priority.urgent", value: 4 },
  { id: "high", labelKey: "priority.high", value: 5 },
  { id: "normal", labelKey: "priority.normal", value: 6 },
  { id: "low", labelKey: "priority.low", value: 7 },
  { id: "backlog", labelKey: "priority.backlog", value: 8 },
] as const satisfies ReadonlyArray<{ id: string; labelKey: MessageKey; value: number }>;

export const DEFAULT_TASK_PRIORITY = 5;

export type TaskSortOption = "priority" | "name" | "updated";

export type TaskPriorityLevel = (typeof TASK_PRIORITY_LEVELS)[number];

export function namedPriorityValue(priority: number): number | null {
  return TASK_PRIORITY_LEVELS.some((level) => level.value === priority) ? priority : null;
}

export function priorityLabel(priority: number): string {
  const level = TASK_PRIORITY_LEVELS.find((item) => item.value === priority);
  return level ? t(level.labelKey) : tf("priority.custom", { value: String(priority) });
}

export function prioritySelectOptions(current: number): { value: number; label: string }[] {
  const options: { value: number; label: string }[] = TASK_PRIORITY_LEVELS.map((level) => ({
    value: level.value,
    label: t(level.labelKey),
  }));
  if (namedPriorityValue(current) === null) {
    options.unshift({ value: current, label: priorityLabel(current) });
  }
  return options;
}

export function compareTaskPriority(
  left: { priority: number; updated_at: string; id?: string },
  right: { priority: number; updated_at: string; id?: string },
): number {
  if (left.priority !== right.priority) {
    return left.priority - right.priority;
  }
  if (left.updated_at !== right.updated_at) {
    return left.updated_at < right.updated_at ? 1 : -1;
  }
  if (left.id && right.id && left.id !== right.id) {
    return left.id < right.id ? 1 : -1;
  }
  return 0;
}

export function sortTasksByPriority<T extends { priority: number; updated_at: string; id?: string }>(
  tasks: readonly T[],
): T[] {
  return tasks.slice().sort(compareTaskPriority);
}

export function sortTasks<T extends { priority: number; updated_at: string; title: string; id?: string }>(
  tasks: readonly T[],
  sort: TaskSortOption,
): T[] {
  return tasks.slice().sort((left, right) => {
    switch (sort) {
      case "priority":
        return compareTaskPriority(left, right);
      case "name":
        return left.title.localeCompare(right.title);
      case "updated":
        return right.updated_at < left.updated_at ? 1 : -1;
      default:
        return compareTaskPriority(left, right);
    }
  });
}
