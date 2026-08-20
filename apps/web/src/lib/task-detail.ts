export const TASK_DETAIL_BOARD_HREF = "/app/board";
export const TASK_DETAIL_BACKLOG_HREF = "/app/backlog";

export type TaskDetailCrumb =
  | { type: "link"; href: string; surface: "board" | "backlog" }
  | { type: "title"; text: string };

export function taskDetailHref(taskId: string, from?: string | null): string {
  if (from === "backlog") {
    return `/app/tasks/${encodeURIComponent(taskId)}?from=backlog`;
  }
  return `/app/tasks/${encodeURIComponent(taskId)}`;
}

export function isTaskDetailFromBacklog(
  search: string | URLSearchParams | null | undefined,
): boolean {
  if (!search) {
    return false;
  }
  const params =
    typeof search === "string"
      ? new URLSearchParams(search.startsWith("?") ? search.slice(1) : search)
      : search;
  return params.get("from") === "backlog";
}

export function taskDetailFallbackHref(): string {
  return TASK_DETAIL_BOARD_HREF;
}

export function taskDetailCrumbs(
  title: string,
  options: { fromBacklog?: boolean } = {},
): TaskDetailCrumb[] {
  const crumbs: TaskDetailCrumb[] = [
    { type: "link", href: TASK_DETAIL_BOARD_HREF, surface: "board" },
  ];
  if (options.fromBacklog) {
    crumbs.push({ type: "link", href: TASK_DETAIL_BACKLOG_HREF, surface: "backlog" });
  }
  crumbs.push({ type: "title", text: title });
  return crumbs;
}
