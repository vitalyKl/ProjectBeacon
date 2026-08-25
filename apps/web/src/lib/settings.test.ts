import { describe, expect, it } from "vitest";

import { SETTINGS_JUMP_LINKS, SETTINGS_THEME_OPTIONS, THEME_OPTION_MESSAGES } from "./settings";
import { THEMES } from "./theme";

describe("settings jump links", () => {
  it("lists Theme, Language, Project, Members, Repositories, Areas, and Webhooks as in-page ids", () => {
    expect(SETTINGS_JUMP_LINKS.map((item) => item.id)).toEqual([
      "theme",
      "language",
      "project",
      "members",
      "repositories",
      "areas",
      "webhooks",
    ]);
    expect(SETTINGS_JUMP_LINKS.every((item) => !item.id.includes("/"))).toBe(true);
  });
});

describe("theme option mapping", () => {
  it("maps every theme to a settings.theme* message", () => {
    expect(THEMES.map((theme) => THEME_OPTION_MESSAGES[theme])).toEqual([
      "settings.themeSystem",
      "settings.themeLight",
      "settings.themeDark",
    ]);
    expect(SETTINGS_THEME_OPTIONS.map((item) => item.value)).toEqual([...THEMES]);
  });
});
