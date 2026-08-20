import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  WORK_AREA_STORAGE_PREFIX,
  readWorkAreaFilter,
  workAreaStorageKey,
  writeWorkAreaFilter,
} from "./use-work-filters";

type MemoryStorage = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

function installStorage(initial: Record<string, string> = {}) {
  const store = new Map(Object.entries(initial));
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
  vi.stubGlobal("window", { localStorage: storage });
  return store;
}

beforeEach(() => {
  installStorage();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("work area filter persist/restore", () => {
  it("uses a project-scoped storage key", () => {
    expect(workAreaStorageKey("proj-1")).toBe(`${WORK_AREA_STORAGE_PREFIX}.proj-1`);
  });

  it("restores empty when nothing is stored", () => {
    expect(readWorkAreaFilter(null)).toBe("");
    expect(readWorkAreaFilter("proj-1")).toBe("");
  });

  it("persists a selected area and restores it for that project", () => {
    writeWorkAreaFilter("proj-1", "lab-api");
    expect(window.localStorage.getItem(workAreaStorageKey("proj-1"))).toBe("lab-api");
    expect(readWorkAreaFilter("proj-1")).toBe("lab-api");
    expect(readWorkAreaFilter("proj-2")).toBe("");
  });

  it("clears the stored area when All areas is selected", () => {
    writeWorkAreaFilter("proj-1", "lab-api");
    writeWorkAreaFilter("proj-1", "");
    expect(window.localStorage.getItem(workAreaStorageKey("proj-1"))).toBeNull();
    expect(readWorkAreaFilter("proj-1")).toBe("");
  });

  it("restores a previously written area after a fresh read", () => {
    writeWorkAreaFilter("proj-1", "lab-web");
    vi.unstubAllGlobals();
    installStorage({ [workAreaStorageKey("proj-1")]: "lab-web" });
    expect(readWorkAreaFilter("proj-1")).toBe("lab-web");
  });
});
