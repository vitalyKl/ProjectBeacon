import type { MessageKey } from "./i18n";
import { THEMES, type Theme } from "./theme";

export const SETTINGS_JUMP_LINKS = [
  { id: "theme", message: "settings.theme" },
  { id: "language", message: "settings.language" },
  { id: "project", message: "common.project" },
  { id: "members", message: "settings.members" },
  { id: "repositories", message: "settings.repositories" },
  { id: "areas", message: "labels.areas" },
  { id: "webhooks", message: "webhooks.title" },
] as const satisfies ReadonlyArray<{ id: string; message: MessageKey }>;

export const THEME_OPTION_MESSAGES = {
  system: "settings.themeSystem",
  light: "settings.themeLight",
  dark: "settings.themeDark",
} as const satisfies Record<Theme, MessageKey>;

export const SETTINGS_THEME_OPTIONS = THEMES.map((value) => ({
  value,
  message: THEME_OPTION_MESSAGES[value],
}));
