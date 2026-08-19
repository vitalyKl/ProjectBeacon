import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  THEME_STORAGE_KEY,
  getTheme,
  hydrateTheme,
  isTheme,
  setTheme,
  subscribeTheme,
} from "./theme";

type MemoryStorage = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

function installDom(initial: Record<string, string> = {}) {
  const store = new Map(Object.entries(initial));
  const attrs = new Map<string, string>();
  const storage: MemoryStorage = {
    getItem(key) {
      return store.get(key) ?? null;
    },
    setItem(key, value) {
      store.set(key, value);
    },
    removeItem(key) {
      store.delete(key);
    },
  };
  const documentElement = {
    setAttribute(name: string, value: string) {
      attrs.set(name, value);
    },
    removeAttribute(name: string) {
      attrs.delete(name);
    },
    getAttribute(name: string) {
      return attrs.get(name) ?? null;
    },
  };
  vi.stubGlobal("window", { localStorage: storage });
  vi.stubGlobal("document", { documentElement });
  return { store, documentElement };
}

beforeEach(() => {
  installDom();
  setTheme("system");
});

afterEach(() => {
  setTheme("system");
  vi.unstubAllGlobals();
});

describe("theme persist and apply", () => {
  it("treats only system, light, and dark as themes", () => {
    expect(isTheme("system")).toBe(true);
    expect(isTheme("light")).toBe(true);
    expect(isTheme("dark")).toBe(true);
    expect(isTheme("neon")).toBe(false);
    expect(getTheme()).toBe("system");
  });

  it("persists the preference and sets data-theme for explicit modes", () => {
    setTheme("dark");
    expect(getTheme()).toBe("dark");
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe("dark");
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");

    setTheme("light");
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe("light");
    expect(document.documentElement.getAttribute("data-theme")).toBe("light");
  });

  it("clears data-theme when the preference is system", () => {
    setTheme("dark");
    setTheme("system");
    expect(getTheme()).toBe("system");
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe("system");
    expect(document.documentElement.getAttribute("data-theme")).toBeNull();
  });

  it("hydrates a stored preference and ignores unknown values", () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, "dark");
    expect(hydrateTheme()).toBe("dark");
    expect(getTheme()).toBe("dark");
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");

    window.localStorage.setItem(THEME_STORAGE_KEY, "neon");
    expect(hydrateTheme()).toBe("system");
    expect(document.documentElement.getAttribute("data-theme")).toBeNull();
  });

  it("notifies subscribers only when the theme changes", () => {
    const seen: string[] = [];
    const stop = subscribeTheme(() => {
      seen.push(getTheme());
    });
    setTheme("dark");
    setTheme("dark");
    setTheme("light");
    stop();
    setTheme("system");
    expect(seen).toEqual(["dark", "light"]);
  });
});
