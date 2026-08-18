export const ORG_STORAGE_KEY = "beacon.org";
export const PROJECT_STORAGE_KEY = "beacon.project";

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
