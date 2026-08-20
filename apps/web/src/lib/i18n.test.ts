import { describe, expect, it } from "vitest";

import { LOCALES, t } from "./i18n";

describe("i18n", () => {
  it("covers every locale key and falls back to English", () => {
    const english = Object.keys(t as unknown as object);
    expect(t("nav.reports")).toBe("Reports");
    expect(t("nav.reports", "es")).toBe("Informes");
    expect(t("nav.reports", "uk")).toBe("Звіти");
    expect(t("nav.reports", "fr")).toBe("Rapports");
    expect(t("nav.reports", "de")).toBe("Berichte");
    expect(t("nav.reports", "pt")).toBe("Relatórios");
    expect(t("nav.reports", "pl")).toBe("Raporty");
    expect(t("nav.reports", "it")).toBe("Report");
    expect(t("nav.reports", "ja")).toBe("レポート");
    expect(t("nav.reports", "zh")).toBe("报告");
    expect(t("nav.reports", "ru")).toBe("Отчёты");
    expect(t("nav.reports", "lt")).toBe("Ataskaitos");
    expect(t("nav.reports", "lv")).toBe("Pārskati");
    expect(t("nav.reports", "be")).toBe("Справаздачы");
    expect(t("nav.reports", "kk")).toBe("Есептер");
    expect(t("nav.reports", "ko")).toBe("보고서");
    const keys = [
      "nav.home",
      "nav.files",
      "nav.reports",
      "nav.learn",
      "nav.group.work",
      "nav.group.record",
      "a11y.skipToMain",
      "command.placeholder",
      "command.noResults",
      "command.open",
      "command.newProject",
      "command.newTask",
      "wizard.newProject",
      "learn.startBody",
      "learn.multiBody",
      "settings.languageHint",
      "settings.theme",
      "settings.themeSystem",
      "settings.themeLight",
      "settings.themeDark",
      "settings.themeHint",
      "settings.attachLocal",
      "files.emptyReposHint",
      "home.welcome",
      "common.priority",
      "priority.urgent",
      "activity.start_work",
    ] as const;
    for (const locale of LOCALES) {
      for (const key of keys) {
        expect(t(key, locale).length).toBeGreaterThan(0);
      }
      expect(t("reports.intro", locale).length).toBeGreaterThan(20);
    }
    void english;
  });

  it("falls back to English when a locale omits a key", () => {
    expect(t("board.hint", "es")).toBe(t("board.hint", "en"));
    expect(t("wizard.newProject", "ko")).toBe("New project");
  });
});
