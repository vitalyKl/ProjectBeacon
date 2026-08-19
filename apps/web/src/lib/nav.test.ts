import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { APP_NAV, LOGIN_PATH, POST_LOGIN_PATH } from "./nav";

const webRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

function readWeb(relativePath: string): string {
  return readFileSync(join(webRoot, relativePath), "utf8");
}

describe("auth paths", () => {
  it("sends a successful local login to /app", () => {
    expect(LOGIN_PATH).toBe("/login");
    expect(POST_LOGIN_PATH).toBe("/app");
    expect(APP_NAV[0]?.href).toBe(POST_LOGIN_PATH);
    expect(APP_NAV.map((item) => item.href)).toEqual([
      "/app",
      "/app/board",
      "/app/backlog",
      "/app/roadmap",
      "/app/context",
      "/app/files",
      "/app/agents",
      "/app/decisions",
      "/app/reports",
      "/app/learn",
      "/app/settings",
    ]);
    expect(readWeb("app/login/login-form.tsx")).toContain("router.replace(POST_LOGIN_PATH)");
    expect(readWeb("app/bootstrap/bootstrap-form.tsx")).toContain(
      "router.replace(POST_LOGIN_PATH)",
    );
  });

  it("keeps the post-login shell from rendering an unbound Link", () => {
    const shell = readWeb("app/app/app-shell.tsx");
    expect(shell).toMatch(/import Link from ["']next\/link["']/);
    expect(shell).toContain("<Link");
    expect(shell).toContain("ToastProvider");
  });

  it("keeps Decisions as a live screen instead of the stub placeholder", () => {
    const page = readWeb("app/app/decisions/page.tsx");
    expect(page).not.toContain("Placeholder");
    expect(page).toContain("DecisionsView");
    expect(readWeb("app/app/decisions/decisions-view.tsx")).toContain("fetchProjectDecisions");
  });

  it("keeps a live Areas catalog on Settings", () => {
    const settings = readWeb("app/app/settings/settings-view.tsx");
    expect(settings).toContain("LabelsCatalog");
    expect(readWeb("app/app/settings/labels-catalog.tsx")).toContain("fetchProjectLabels");
    expect(readWeb("app/app/settings/labels-catalog.tsx")).toContain("labels.saveArea");
    expect(readWeb("app/app/settings/labels-catalog.tsx")).toContain("labels.addPrefix");
  });

  it("advises starter areas when creating a project", () => {
    const wizard = readWeb("app/app/projects/new/wizard.tsx");
    expect(wizard).toContain("fetchProjectLabels");
    expect(wizard).toContain("wizard.attachHint");
    expect(wizard).toContain("label_ids");
  });

  it("loads Home index status from the project repos instead of a hardcoded offline card", () => {
    const home = readWeb("app/app/page.tsx");
    expect(home).toContain("fetchDetailedProjectRepos");
    expect(home).toContain("pickHomeIndexRepo");
    expect(home).not.toContain("Not connected");
    expect(home).not.toMatch(/>Offline</);
  });

  it("shows the project brief as section blocks and offers ready tasks to agents", () => {
    const home = readWeb("app/app/page.tsx");
    expect(home).toContain("fetchContextNodes");
    expect(home).toContain("home.brief");
    expect(home).toContain("home.ready");
    expect(home).toContain("common.compileBrief");
    expect(home).toContain("/app/learn");
    expect(readWeb("app/app/agents/agents-view.tsx")).toContain("agents.ready");
    expect(readWeb("app/app/agents/agents-view.tsx")).toContain("agents.intro");
    expect(readWeb("app/app/agents/agents-view.tsx")).toContain("agents.offered");
    expect(readWeb("lib/i18n.ts")).toContain("start_work");
    expect(readWeb("lib/i18n.ts")).toContain("beacon setup");
    expect(readWeb("lib/i18n.ts")).toContain("setup.cmd");
    expect(readWeb("app/app/agents/agents-view.tsx")).toContain("CopyableProjectId");
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("CopyableProjectId");
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("LanguagePicker");
    expect(readWeb("app/app/learn/page.tsx")).toContain("learn.startBody");
    expect(readWeb("app/app/learn/page.tsx")).toContain("learn.files");
    expect(readWeb("app/app/files/page.tsx")).toContain("FilesView");
    expect(readWeb("app/app/files/files-view.tsx")).toContain("fetchRepoTree");
    expect(readWeb("app/app/files/files-view.tsx")).toContain("fetchRepoFile");
    expect(readWeb("app/app/reports/page.tsx")).toContain("fetchProjectReports");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("common.compileBrief");
    expect(readWeb("app/app/create-task-form.tsx")).toContain("PrioritySelect");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("PrioritySelect");
    expect(readWeb("app/app/board/page.tsx")).toContain("sortTasksByPriority");
    expect(readWeb("app/app/backlog/page.tsx")).toContain("sortTasksByPriority");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("common.compileBrief");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("takeInputFiles");
    expect(readWeb("app/app/context/context-editor.tsx")).not.toMatch(
      /const files = event\.target\.files;\s*event\.target\.value = "";/,
    );
  });

  it("keeps Definition of Done and tech stack as first-class brief sections", () => {
    const editor = readWeb("app/app/context/context-editor.tsx");
    expect(editor).toContain('id: "definition_of_done"');
    expect(editor).toContain('id: "stack"');
    expect(editor).toContain("knownSectionTitle");
    expect(editor).toContain("context.section.${id}");
    const wizard = readWeb("app/app/projects/new/wizard.tsx");
    expect(wizard).toContain('id: "definition_of_done"');
    expect(wizard).toContain('id: "stack"');
    expect(wizard).toContain('id: "commands"');
  });
});
