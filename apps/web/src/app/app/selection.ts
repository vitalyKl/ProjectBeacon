import type { PublicMe, PublicOrg, PublicProject } from "@/lib/api";

export const ORG_STORAGE_KEY = "beacon.org";
export const PROJECT_STORAGE_KEY = "beacon.project";

export function pickOrg(me: PublicMe, storedId: string | null): PublicOrg | null {
  if (storedId) {
    const stored = me.orgs.find((org) => org.id === storedId);
    if (stored) {
      return stored;
    }
  }
  return me.personal_org ?? me.orgs[0] ?? null;
}

export function pickProject(
  projects: PublicProject[],
  storedId: string | null,
): PublicProject | null {
  if (storedId) {
    const stored = projects.find((project) => project.id === storedId);
    if (stored) {
      return stored;
    }
  }
  return projects[0] ?? null;
}

export function readStoredId(key: string): string | null {
  if (typeof window === "undefined") {
    return null;
  }
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeStoredId(key: string, value: string | null): void {
  if (typeof window === "undefined") {
    return;
  }
  try {
    if (value) {
      window.localStorage.setItem(key, value);
    } else {
      window.localStorage.removeItem(key);
    }
  } catch {
    // private mode
  }
}
