import { TASK_STATUSES, type TaskStatus } from "./roadmap";

export const BUTTON_VARIANTS = ["primary", "secondary", "danger"] as const;
export type ButtonVariant = (typeof BUTTON_VARIANTS)[number];

export const BANNER_TONES = ["neutral", "danger", "warning", "success"] as const;
export type BannerTone = (typeof BANNER_TONES)[number];

export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}

export function statusTokenName(status: TaskStatus): string {
  return `status-${status.replaceAll("_", "-")}`;
}

export function statusToneClass(kind: "text" | "bg", status: TaskStatus): string {
  return `${kind}-${statusTokenName(status)}`;
}

export function statusFillVar(status: TaskStatus): string {
  return `var(--${statusTokenName(status)})`;
}

export const STATUS_TEXT_CLASS = Object.fromEntries(
  TASK_STATUSES.map((status) => [status, statusToneClass("text", status)]),
) as Record<TaskStatus, string>;

export const STATUS_BG_CLASS = Object.fromEntries(
  TASK_STATUSES.map((status) => [status, statusToneClass("bg", status)]),
) as Record<TaskStatus, string>;

export const STATUS_FILL_VAR = Object.fromEntries(
  TASK_STATUSES.map((status) => [status, statusFillVar(status)]),
) as Record<TaskStatus, string>;

export const BUTTON_VARIANT_CLASS: Record<ButtonVariant, string> = {
  primary:
    "rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60",
  secondary: "rounded-md border border-border px-3 py-1.5 text-sm disabled:opacity-60",
  danger: "rounded-md bg-danger px-3 py-1.5 text-sm font-medium text-danger-fg disabled:opacity-60",
};

export const BANNER_TONE_CLASS: Record<BannerTone, string> = {
  neutral: "border border-border bg-surface px-4 py-2 text-sm text-foreground",
  danger: "border border-danger bg-danger/10 px-4 py-2 text-sm text-danger",
  warning: "border border-warning bg-warning/10 px-4 py-2 text-sm text-warning",
  success: "border border-success bg-success/10 px-4 py-2 text-sm text-success",
};

export const FIELD_INPUT_CLASS = "h-9 rounded-md border border-border bg-background px-2 text-sm";
export const FIELD_TEXTAREA_CLASS =
  "min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm";
export const FIELD_ERROR_CLASS = "text-sm text-danger";

export const PANEL_CLASS = "rounded-lg border border-border bg-surface p-4";
export const PAGE_TITLE_CLASS = "text-2xl font-semibold tracking-tight";
export const PAGE_DESCRIPTION_CLASS = "text-sm text-muted";
export const EMPTY_TITLE_CLASS = "text-lg font-semibold tracking-tight";
export const EMPTY_DESCRIPTION_CLASS = "max-w-xl text-sm leading-6 text-muted";
export const SEGMENTED_GROUP_CLASS =
  "flex rounded-md border border-border bg-surface p-0.5 text-sm";
export const SEGMENTED_ITEM_CLASS = "rounded px-3 py-1.5 text-muted";
export const SEGMENTED_ITEM_ACTIVE_CLASS = "bg-background font-medium text-foreground";

export function segmentedItemClass(active: boolean): string {
  return cx(SEGMENTED_ITEM_CLASS, active && SEGMENTED_ITEM_ACTIVE_CLASS);
}
