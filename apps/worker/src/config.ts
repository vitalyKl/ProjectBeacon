import { loadGithubAppConfig, type GithubAppConfig } from "./github-app.js";

export type WorkerConfig = {
  apiUrl: string;
  workerToken: string;
  databaseUrl: string;
  workspace: string | undefined;
  indexRpcHost: string;
  indexRpcPort: number;
  indexRpcToken: string;
  indexDir: string;
  cloneDir: string;
  githubApp?: GithubAppConfig;
};

export function loadWorkerConfig(env: NodeJS.ProcessEnv = process.env): WorkerConfig {
  const apiUrl = (env["BEACON_API_URL"] ?? "http://api:8080").replace(/\/+$/, "");
  const workerToken = env["BEACON_WORKER_TOKEN"] ?? "";
  const databaseUrl = env["DATABASE_URL"] ?? "";
  const workspace = env["BEACON_WORKSPACE"]?.trim() || undefined;
  const indexRpcHost = env["INDEX_RPC_HOST"]?.trim() || "0.0.0.0";
  const indexRpcPort = Number.parseInt(env["INDEX_RPC_PORT"] ?? "7744", 10);
  const indexRpcToken = env["INDEX_RPC_TOKEN"] ?? "";
  const indexDir = env["BEACON_INDEX_DIR"]?.trim() || "/var/lib/beacon/index";
  const cloneDir = env["BEACON_CLONE_DIR"]?.trim() || "/var/lib/beacon/clones";
  return {
    apiUrl,
    workerToken,
    databaseUrl,
    workspace,
    indexRpcHost,
    indexRpcPort: Number.isInteger(indexRpcPort) ? indexRpcPort : 7744,
    indexRpcToken,
    indexDir,
    cloneDir,
    githubApp: loadGithubAppConfig(env),
  };
}
