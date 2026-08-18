export const LOGIN_PATH = "/login";
export const POST_LOGIN_PATH = "/app";

export const APP_NAV = [
  { href: POST_LOGIN_PATH, label: "Home" },
  { href: "/app/board", label: "Board" },
  { href: "/app/backlog", label: "Backlog" },
  { href: "/app/roadmap", label: "Roadmap" },
  { href: "/app/context", label: "Context" },
  { href: "/app/agents", label: "Agents" },
  { href: "/app/decisions", label: "Decisions" },
  { href: "/app/settings", label: "Settings" },
] as const;

export type AppSection = (typeof APP_NAV)[number]["label"];
