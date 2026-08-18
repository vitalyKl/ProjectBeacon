import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const PREBUILD_PLATFORMS = [
  "win32-x64",
  "darwin-arm64",
  "darwin-x64",
  "linux-x64-gnu",
  "linux-arm64-gnu",
] as const;

export type PrebuildPlatform = (typeof PREBUILD_PLATFORMS)[number];

export function currentPrebuildPlatform(
  platform: NodeJS.Platform = process.platform,
  arch: string = process.arch,
): PrebuildPlatform | undefined {
  if (platform === "win32" && arch === "x64") {
    return "win32-x64";
  }
  if (platform === "darwin" && arch === "arm64") {
    return "darwin-arm64";
  }
  if (platform === "darwin" && arch === "x64") {
    return "darwin-x64";
  }
  if (platform === "linux" && arch === "x64") {
    return "linux-x64-gnu";
  }
  if (platform === "linux" && arch === "arm64") {
    return "linux-arm64-gnu";
  }
  return undefined;
}

export function ripgrepBinaryName(platform: PrebuildPlatform): string {
  return platform.startsWith("win32") ? "rg.exe" : "rg";
}

export function prebuildsRoot(): string {
  return path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "prebuilds");
}

export function resolveRipgrepPath(env: NodeJS.ProcessEnv = process.env): string {
  const override = env["BEACON_RG"]?.trim();
  if (override) {
    return override;
  }
  const platform = currentPrebuildPlatform();
  if (platform) {
    const bundled = path.join(prebuildsRoot(), platform, ripgrepBinaryName(platform));
    if (fs.existsSync(bundled)) {
      return bundled;
    }
  }
  return process.platform === "win32" ? "rg.exe" : "rg";
}
