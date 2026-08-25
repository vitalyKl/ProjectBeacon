import type { MessageKey } from "./i18n";

export const LOGIN_PATH = "/login";
export const POST_LOGIN_PATH = "/app";
export const NEW_PROJECT_PATH = "/app/projects/new";

export const NAV_GROUPS = ["work", "record", "utility"] as const;
export type NavGroup = (typeof NAV_GROUPS)[number];

export const NAV_GROUP_MESSAGE: Record<NavGroup, MessageKey | null> = {
  work: "nav.group.work",
  record: "nav.group.record",
  utility: null,
};

export type AppNavItem = {
  href: string;
  label: string;
  message: MessageKey;
  group: NavGroup;
  navVisible?: false;
};

export const APP_NAV = [
  { href: POST_LOGIN_PATH, label: "Home", message: "nav.home", group: "work" },
  { href: "/app/board", label: "Board", message: "nav.board", group: "work" },
  {
    href: "/app/backlog",
    label: "Backlog",
    message: "nav.backlog",
    group: "work",
    navVisible: false,
  },
  { href: "/app/roadmap", label: "Roadmap", message: "nav.roadmap", group: "work" },
  { href: "/app/workspace", label: "Workspace", message: "nav.workspace", group: "work" },
  { href: "/app/agents", label: "Agents", message: "nav.agents", group: "work" },
  { href: "/app/decisions", label: "Decisions", message: "nav.decisions", group: "record" },
  { href: "/app/reports", label: "Reports", message: "nav.reports", group: "record" },
  {
    href: "/app/context",
    label: "Context",
    message: "nav.context",
    group: "utility",
    navVisible: false,
  },
  {
    href: "/app/files",
    label: "Files",
    message: "nav.files",
    group: "utility",
    navVisible: false,
  },
  {
    href: "/app/learn",
    label: "Learn",
    message: "nav.learn",
    group: "utility",
    navVisible: false,
  },
  { href: "/app/settings", label: "Settings", message: "nav.settings", group: "utility" },
] as const satisfies ReadonlyArray<AppNavItem>;

export type AppSection = (typeof APP_NAV)[number]["label"];

export function isNavVisible(item: (typeof APP_NAV)[number]): boolean {
  return !("navVisible" in item) || item.navVisible !== false;
}

export const APP_NAV_VISIBLE = APP_NAV.filter(isNavVisible);

export function navMessageForHref(href: string): MessageKey {
  return APP_NAV.find((item) => item.href === href)?.message ?? "nav.home";
}

export function isNavItemActive(pathname: string, href: string): boolean {
  return href === POST_LOGIN_PATH
    ? pathname === href
    : pathname === href || pathname.startsWith(`${href}/`);
}

export function navGroupMessage(group: NavGroup, previous?: NavGroup): MessageKey | null {
  if (previous === group) {
    return null;
  }
  return NAV_GROUP_MESSAGE[group];
}
