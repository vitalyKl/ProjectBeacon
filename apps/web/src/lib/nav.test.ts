import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  APP_NAV,
  APP_NAV_VISIBLE,
  LOGIN_PATH,
  NEW_PROJECT_PATH,
  POST_LOGIN_PATH,
  isNavItemActive,
  isNavVisible,
  navGroupMessage,
} from "./nav";

const webRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

function readWeb(relativePath: string): string {
  return readFileSync(join(webRoot, relativePath), "utf8");
}

const APP_NAV_HREFS = [
  "/app",
  "/app/board",
  "/app/backlog",
  "/app/roadmap",
  "/app/context",
  "/app/agents",
  "/app/decisions",
  "/app/reports",
  "/app/files",
  "/app/learn",
  "/app/settings",
] as const;

describe("auth paths", () => {
  it("sends a successful local login to /app", () => {
    expect(LOGIN_PATH).toBe("/login");
    expect(POST_LOGIN_PATH).toBe("/app");
    expect(APP_NAV[0]?.href).toBe(POST_LOGIN_PATH);
    expect(APP_NAV.map((item) => item.href)).toEqual([...APP_NAV_HREFS]);
    expect(readWeb("app/login/login-form.tsx")).toContain("router.replace(POST_LOGIN_PATH)");
    expect(readWeb("app/login/login-form.tsx")).toContain("@/lib/ui/");
    expect(readWeb("app/login/page.tsx")).toContain("@/lib/ui/");
    expect(readWeb("app/login/page.tsx")).not.toContain("app/app/");
    expect(readWeb("app/bootstrap/bootstrap-form.tsx")).toContain(
      "router.replace(POST_LOGIN_PATH)",
    );
    expect(readWeb("app/bootstrap/page.tsx")).toContain("@/lib/ui/");
    expect(readWeb("app/bootstrap/page.tsx")).not.toContain("app/app/");
    expect(readWeb("app/page.tsx")).toContain("@/lib/ui/");
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
    expect(readWeb("app/app/decisions/decisions-view.tsx")).toContain("patchDecision");
    expect(readWeb("app/app/decisions/decisions-view.tsx")).toContain("decisions.accept");
  });

  it("does not keep an unused Placeholder stub", () => {
    expect(() => readWeb("app/app/placeholder.tsx")).toThrow();
    expect(readWeb("app/app/decisions/page.tsx")).not.toContain("Placeholder");
    expect(readWeb("app/app/reports/page.tsx")).not.toContain("Placeholder");
    expect(readWeb("app/app/files/page.tsx")).not.toContain("Placeholder");
    expect(readWeb("app/app/learn/page.tsx")).not.toContain("Placeholder");
    expect(readWeb("app/app/settings/page.tsx")).not.toContain("Placeholder");
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
    const home = readWeb("app/app/home-view.tsx");
    const card = readWeb("app/app/index-status-card.tsx");
    expect(readWeb("app/app/page.tsx")).toContain("publicFlags");
    expect(home).toContain("fetchDetailedProjectRepos");
    expect(home).toContain("pickHomeIndexRepo");
    expect(home).toContain("IndexStatusCard");
    expect(card).toContain("indexModeLabel(repo?.index_mode, { hostedClone })");
    expect(home).not.toContain("Not connected");
    expect(home).not.toMatch(/>Offline</);
    expect(card).not.toContain("hosted clone");
  });

  it("keeps Home as a pulse: Goals and DoD, queue peek, milestones, agents, index", () => {
    const home = readWeb("app/app/home-view.tsx");
    const wizard = readWeb("app/app/projects/new/wizard.tsx");
    expect(home).toContain("fetchContextNodes");
    expect(home).toContain("pickPulseBriefSections");
    expect(home).toContain("countHomeQueue");
    expect(home).toContain("peekReadyTasks");
    expect(home).toContain("countOpenMilestones");
    expect(home).toContain("fetchProjectMilestones");
    expect(home).toContain("fetchProjectSessions");
    expect(home).toContain("fetchProjectActivity");
    expect(home).toContain("NEW_PROJECT_PATH");
    expect(home).toContain("/app/roadmap");
    expect(home).toContain("home.brief");
    expect(home).toContain("/app/learn");
    expect(home).not.toContain("FirstProjectForm");
    expect(home).not.toContain("common.compileBrief");
    expect(home).not.toContain("createOrgProject");
    expect(home).not.toContain("sections.slice(0, 2)");
    expect(home).not.toContain("sections.slice(0,2)");
    expect(wizard).not.toContain("AgentTab");
    expect(wizard).not.toContain("wizard.http");
    expect(wizard).not.toContain("hosted clone");
    expect(wizard).toContain("wizard.stdioHint");
    expect(readWeb("lib/locales/en.ts")).toContain("start_work");
    expect(readWeb("lib/locales/en.ts")).toContain("beacon setup");
    expect(readWeb("lib/locales/en.ts")).toContain("setup.cmd");
    expect(readWeb("lib/locales/en.ts")).toContain(
      "Home is a pulse: Goals and Definition of Done, a queue peek, open milestones, active agents, and index status.",
    );
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("CopyableProjectId");
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("LanguagePicker");
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("setTheme");
    expect(readWeb("app/app/settings/settings-view.tsx")).not.toContain("/app/settings/members");
    expect(readWeb("app/app/learn/page.tsx")).toContain("learn.startBody");
    expect(readWeb("app/app/learn/page.tsx")).toContain("learn.files");
    expect(readWeb("app/app/files/page.tsx")).toContain("FilesView");
    expect(readWeb("app/app/files/files-view.tsx")).toContain("fetchRepoTree");
    expect(readWeb("app/app/files/files-view.tsx")).toContain("fetchRepoFile");
    expect(readWeb("app/app/files/files-view.tsx")).toContain("filesEmptyKind");
    expect(readWeb("app/app/files/files-view.tsx")).not.toContain('href="/app/settings"');
    expect(readWeb("app/app/reports/page.tsx")).toContain("fetchProjectReports");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("common.compileBrief");
    expect(readWeb("app/app/create-task-form.tsx")).toContain("PrioritySelect");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("PrioritySelect");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("lg:grid-cols-2");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("taskDetailFallbackHref");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("taskDetailCrumbs");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).toContain("BriefBlocks");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).not.toContain("document.referrer");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).not.toContain("nav.group.work");
    expect(readWeb("app/app/tasks/[id]/page.tsx")).not.toContain("write_handoff");
    expect(readWeb("app/app/home-view.tsx")).toContain("BriefBlocks");
    expect(readWeb("app/app/context/preview-revisions.tsx")).toContain("BriefBlocks");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("fetchProjectRepos");
    expect(readWeb("app/app/context/section-drafts.tsx")).toContain("repoPickerOptions");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("projectRepoCatalog");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain('setCreateRepoId("")');
    expect(readWeb("app/app/context/section-drafts.tsx")).toContain("context.failedRepos");
    expect(readWeb("app/app/context/context-editor.tsx")).not.toContain("function BriefView");
    expect(readWeb("app/app/context/preview-revisions.tsx")).not.toContain("function BriefView");
    expect(readWeb("app/app/context/context-editor.tsx")).not.toContain("write_handoff");
    expect(readWeb("app/app/board/page.tsx")).toContain("sortTasksByPriority");
    expect(readWeb("app/app/backlog/page.tsx")).toContain("sortTasksByPriority");
    expect(readWeb("app/app/context/import-export.tsx")).toContain("common.compileBrief");
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("takeInputFiles");
    expect(readWeb("app/app/context/context-editor.tsx")).not.toMatch(
      /const files = event\.target\.files;\s*event\.target\.value = "";/,
    );
    expect(readWeb("app/app/context/context-editor.tsx")).toContain("createContextNode");
    expect(readWeb("app/app/context/context-editor.tsx")).not.toContain("randomUuidV7");
  });

  it("keeps Agents as sessions, tokens, and activity with setup steps", () => {
    const agents = readWeb("app/app/agents/agents-view.tsx");
    const i18n = readWeb("lib/locales/en.ts");
    expect(agents).toContain("agents.intro");
    expect(agents).toContain("agents.sessions");
    expect(agents).toContain("agents.tokens");
    expect(agents).toContain("agents.activity");
    expect(agents).toContain('href="/app/board"');
    expect(agents).toContain('href="/app/settings"');
    expect(i18n).toContain("setup.cmd");
    expect(i18n).toContain("./setup.sh");
    expect(i18n).toContain("beacon setup");
    expect(i18n).toContain("Ready lives on Board");
    expect(i18n).toContain("project id lives in Settings");
    expect(agents).not.toContain("agents.ready");
    expect(agents).not.toContain("agents.offered");
    expect(agents).not.toContain("CopyableProjectId");
    expect(readWeb("app/app/settings/settings-view.tsx")).toContain("CopyableProjectId");
  });

  it("keeps Definition of Done and tech stack as first-class brief sections", () => {
    const sections = readWeb("lib/context-sections.ts");
    expect(sections).toContain('"definition_of_done"');
    expect(sections).toContain('"stack"');
    expect(sections).toContain("knownSectionTitle");
    expect(sections).toContain("context.section.${id}");
    expect(readWeb("app/app/context/section-drafts.tsx")).toContain("knownSectionTitle");
    const wizard = readWeb("app/app/projects/new/wizard.tsx");
    expect(wizard).toContain("wizardBriefSections");
    expect(wizard).toContain("context.section.definition_of_done");
    expect(wizard).toContain("context.section.stack");
    expect(wizard).toContain("context.section.commands");
    expect(wizard).not.toContain('title: "Goals"');
  });
});

describe("app nav grouping", () => {
  it("assigns a group to every item and keeps Backlog off the scroller", () => {
    expect(APP_NAV).toHaveLength(11);
    expect(APP_NAV.every((item) => item.group)).toBe(true);
    const hidden = APP_NAV.filter((item) => !isNavVisible(item));
    expect(hidden.map((item) => item.href)).toEqual(["/app/backlog"]);
    expect(APP_NAV_VISIBLE.map((item) => item.href)).toEqual(
      APP_NAV_HREFS.filter((href) => href !== "/app/backlog"),
    );
    expect(APP_NAV_VISIBLE.map((item) => item.href)).not.toContain("/app/backlog");
    expect(APP_NAV.find((item) => item.href === "/app/files")?.group).toBe("utility");
    expect(APP_NAV.find((item) => item.href === "/app/decisions")?.group).toBe("record");
    expect(APP_NAV.find((item) => item.href === "/app/board")?.group).toBe("work");
  });

  it("labels work and record only when the group changes", () => {
    expect(navGroupMessage("work")).toBe("nav.group.work");
    expect(navGroupMessage("work", "work")).toBeNull();
    expect(navGroupMessage("record", "work")).toBe("nav.group.record");
    expect(navGroupMessage("record", "record")).toBeNull();
    expect(navGroupMessage("utility", "record")).toBeNull();
  });

  it("treats Home as exact-path only so nested /app routes stay inactive", () => {
    expect(isNavItemActive("/app", "/app")).toBe(true);
    expect(isNavItemActive("/app/board", "/app")).toBe(false);
    expect(isNavItemActive("/app/board/extra", "/app/board")).toBe(true);
    expect(isNavItemActive("/app/backlog", "/app/board")).toBe(false);
  });

  it("renders a skip link, language control, New project, and a narrow scroller without a hamburger", () => {
    const shell = readWeb("app/app/app-shell.tsx");
    expect(shell).toContain("a11y.skipToMain");
    expect(shell).toContain('href="#main"');
    expect(shell).toContain("hydrateTheme");
    expect(shell).toContain("NEW_PROJECT_PATH");
    expect(shell).toContain("wizard.newProject");
    expect(shell).toContain("common.language");
    expect(shell).toContain("APP_NAV_VISIBLE");
    expect(shell).toContain("overflow-x-auto");
    expect(shell).toContain("md:hidden");
    expect(shell).not.toMatch(/hamburger/i);
    expect(shell).toContain("CommandPalette");
    expect(shell).not.toContain("command palette");
    expect(shell).not.toContain("LocaleAttribute");
    expect(NEW_PROJECT_PATH).toBe("/app/projects/new");
    expect(readWeb("app/app/projects/new/page.tsx")).toContain("ProjectWizard");
    expect(readWeb("app/app/backlog/page.tsx")).toContain("nav.backlog");
    expect(readWeb("app/app/backlog/page.tsx")).toContain('from="backlog"');
    expect(readWeb("app/app/task-card.tsx")).toContain("taskDetailHref");
  });
});
