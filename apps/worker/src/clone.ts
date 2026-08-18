import { spawn } from "node:child_process";
import { mkdir, rename, rm, stat } from "node:fs/promises";
import path from "node:path";

import {
  createInstallationToken,
  normalizeCloneRemote,
  type GithubAppConfig,
} from "./github-app.js";

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

export function redactGitText(value: string): string {
  return value
    .replace(/x-access-token:[^@\s]+@/gi, "x-access-token:***@")
    .replace(/(authorization:\s*bearer\s+)(\S+)/gi, "$1***")
    .replace(/\bghs_[A-Za-z0-9_]+/g, "ghs_***");
}

async function runGit(
  args: string[],
  options: { cwd?: string; env?: NodeJS.ProcessEnv } = {},
): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn("git", args, {
      cwd: options.cwd,
      env: options.env,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let stderr = "";
    child.stderr.on("data", (chunk: Buffer) => {
      stderr += chunk.toString();
    });
    child.on("error", (error) => {
      reject(new Error(redactGitText(error.message)));
    });
    child.on("close", (code) => {
      if (code === 0) {
        resolve();
        return;
      }
      const command = args.find((arg) => !arg.startsWith("-")) ?? args[0] ?? "git";
      reject(new Error(`git ${command} failed: ${redactGitText(stderr.trim() || String(code))}`));
    });
  });
}

function gitAuthEnv(token: string): NodeJS.ProcessEnv {
  return {
    ...process.env,
    GIT_TERMINAL_PROMPT: "0",
    GIT_CONFIG_COUNT: "1",
    GIT_CONFIG_KEY_0: "http.extraHeader",
    GIT_CONFIG_VALUE_0: `Authorization: Bearer ${token}`,
  };
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
  const remote = normalizeCloneRemote(repo.remote_url);
  const branch = repo.default_branch || "main";
  const authEnv = gitAuthEnv(token);
  if (options.force || !(await exists(path.join(dest, ".git")))) {
    const staging = `${dest}.tmp`;
    await rm(staging, { recursive: true, force: true });
    await rm(dest, { recursive: true, force: true });
    await runGit(
      ["clone", "--depth", "1", "--branch", branch, "--single-branch", remote, staging],
      { env: authEnv },
    );
    await rename(staging, dest);
  } else {
    await runGit(["fetch", "--depth", "1", "origin", branch], { cwd: dest, env: authEnv });
    await runGit(["checkout", "-B", branch, "FETCH_HEAD"], { cwd: dest });
  }
  await runGit(["remote", "set-url", "origin", remote], { cwd: dest });
  return dest;
}

export async function removeHostedClone(cloneDir: string, repoId: string): Promise<void> {
  await rm(cloneDirForRepo(cloneDir, repoId), { recursive: true, force: true });
}
