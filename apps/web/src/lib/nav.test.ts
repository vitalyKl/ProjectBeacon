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
});
