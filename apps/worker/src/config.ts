export type WorkerConfig = {
  apiUrl: string;
  workerToken: string;
  databaseUrl: string;
  workspace: string | undefined;
};

export function loadWorkerConfig(env: NodeJS.ProcessEnv = process.env): WorkerConfig {
  const apiUrl = (env["BEACON_API_URL"] ?? "http://api:8080").replace(/\/+$/, "");
  const workerToken = env["BEACON_WORKER_TOKEN"] ?? "";
  const databaseUrl = env["DATABASE_URL"] ?? "";
  const workspace = env["BEACON_WORKSPACE"]?.trim() || undefined;
  return { apiUrl, workerToken, databaseUrl, workspace };
}
