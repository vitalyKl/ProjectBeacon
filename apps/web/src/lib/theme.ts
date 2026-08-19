export const THEMES = ["system", "light", "dark"] as const;

export type Theme = (typeof THEMES)[number];

export const THEME_STORAGE_KEY = "beacon.theme";

let currentTheme: Theme = "system";
const listeners = new Set<() => void>();

export function isTheme(value: string): value is Theme {
  return (THEMES as readonly string[]).includes(value);
}

export function getTheme(): Theme {
  return currentTheme;
}

function applyTheme(theme: Theme): void {
  if (typeof document === "undefined") {
    return;
  }
  if (theme === "system") {
    document.documentElement.removeAttribute("data-theme");
    return;
  }
  document.documentElement.setAttribute("data-theme", theme);
}

export function setTheme(theme: Theme): void {
  const changed = currentTheme !== theme;
  currentTheme = theme;
  if (typeof window !== "undefined") {
    window.localStorage.setItem(THEME_STORAGE_KEY, theme);
    applyTheme(theme);
  }
  if (!changed) {
    return;
  }
  for (const listener of listeners) {
    listener();
  }
}

export function hydrateTheme(): Theme {
  if (typeof window === "undefined") {
    return currentTheme;
  }
  const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
  currentTheme = stored && isTheme(stored) ? stored : "system";
  applyTheme(currentTheme);
  return currentTheme;
}

export function subscribeTheme(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
