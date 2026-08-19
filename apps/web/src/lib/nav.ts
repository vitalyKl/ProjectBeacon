import type { MessageKey } from "./i18n";

export const LOGIN_PATH = "/login";
export const POST_LOGIN_PATH = "/app";

export const APP_NAV = [
  { href: POST_LOGIN_PATH, label: "Home", message: "nav.home" },
  { href: "/app/board", label: "Board", message: "nav.board" },
  { href: "/app/backlog", label: "Backlog", message: "nav.backlog" },
  { href: "/app/roadmap", label: "Roadmap", message: "nav.roadmap" },
  { href: "/app/context", label: "Context", message: "nav.context" },
  { href: "/app/files", label: "Files", message: "nav.files" },
  { href: "/app/agents", label: "Agents", message: "nav.agents" },
  { href: "/app/decisions", label: "Decisions", message: "nav.decisions" },
  { href: "/app/reports", label: "Reports", message: "nav.reports" },
  { href: "/app/learn", label: "Learn", message: "nav.learn" },
  { href: "/app/settings", label: "Settings", message: "nav.settings" },
] as const satisfies ReadonlyArray<{ href: string; label: string; message: MessageKey }>;

export type AppSection = (typeof APP_NAV)[number]["label"];

export function navMessageForHref(href: string): MessageKey {
  return APP_NAV.find((item) => item.href === href)?.message ?? "nav.home";
}
