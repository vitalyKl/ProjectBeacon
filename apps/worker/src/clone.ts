import { spawn } from "node:child_process";
import { mkdir, rename, rm, stat } from "node:fs/promises";
import path from "node:path";

import { cloneUrlWithToken, createInstallationToken, type GithubAppConfig } from "./github-app.js";

export type HostedCloneRepo = {
  id: string;
  provider: string;
  remote_url: string | null;
  default_branch: string;
  installation_id: string | null;
};

export function cloneDirForRepo(cloneDir: string, repoId: string): string {
  return path.join(cloneDir, repoId);
}

async function runGit(args: string[], cwd?: string): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn("git", args, {
      cwd,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let stderr = "";
    child.stderr.on("data", (chunk: Buffer) => {
      stderr += chunk.toString();
    });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(`git ${args[0] ?? ""} failed: ${stderr.trim() || String(code)}`));
    });
  });
}

async function exists(target: string): Promise<boolean> {
  try {
    await stat(target);
    return true;
  } catch {
    return false;
  }
}

export async function ensureHostedClone(
  repo: HostedCloneRepo,
  options: {
    cloneDir: string;
    githubApp: GithubAppConfig;
    force?: boolean;
    fetchImpl?: typeof fetch;
  },
): Promise<string> {
  if (repo.provider !== "github" || !repo.remote_url || !repo.installation_id) {
    throw new Error("hosted clone requires a GitHub App installation");
  }
  const dest = cloneDirForRepo(options.cloneDir, repo.id);
  await mkdir(options.cloneDir, { recursive: true });
  const token = await createInstallationToken(
    options.githubApp,
    repo.installation_id,
    options.fetchImpl,
  );
  const remote = cloneUrlWithToken(repo.remote_url, token);
  const branch = repo.default_branch || "main";
  if (options.force || !(await exists(path.join(dest, ".git")))) {
    const staging = `${dest}.tmp`;
    await rm(staging, { recursive: true, force: true });
    await rm(dest, { recursive: true, force: true });
    await runGit(["clone", "--depth", "1", "--branch", branch, "--single-branch", remote, staging]);
    await rename(staging, dest);
    return dest;
  }
  await runGit(["remote", "set-url", "origin", remote], dest);
  await runGit(["fetch", "--depth", "1", "origin", branch], dest);
  await runGit(["checkout", "-B", branch, "FETCH_HEAD"], dest);
  return dest;
}

export async function removeHostedClone(cloneDir: string, repoId: string): Promise<void> {
  await rm(cloneDirForRepo(cloneDir, repoId), { recursive: true, force: true });
}
