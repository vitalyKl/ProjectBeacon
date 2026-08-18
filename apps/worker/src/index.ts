import { pathToFileURL } from "node:url";
import { createWorkerApi } from "./client.js";
import { loadWorkerConfig } from "./config.js";
import { runDetectJob } from "./detect.js";
import { DETECT_QUEUE, isDetectJobData } from "./jobs.js";
import { createWorkerApi, loadWorkerEnv } from "./api.js";
export { createWorkerApi, loadWorkerEnv } from "./api.js";
export { EXPIRE_LOCKS_CRON, EXPIRE_LOCKS_QUEUE, RETENTION_CRON, RETENTION_QUEUE, runExpireLocksJob, runRetentionJob, import { runGithubImportJob, runGithubInvalidateJob } from "./github.js";
import { startWorkerIndexHttp, WorkerIndexRegistry } from "./index-server.js";
import { purgeDeletedProjectClones } from "./purge.js";

import { loadOtelConfig, observeJob, writeLog } from "@beacon/shared";

export const packageName = "@beacon/worker";

async function main(): Promise<void> {
  const config = loadWorkerConfig();
  if (!config.databaseUrl) {
    throw new Error("DATABASE_URL is required");
  }
  if (!config.workerToken) {
    throw new Error("BEACON_WORKER_TOKEN is required");
  }
  if (!config.indexRpcToken) {
    throw new Error("INDEX_RPC_TOKEN is required");
  }

  const otel = loadOtelConfig(process.env, "beacon-worker");
  if (otel.enabled) {
    writeLog({
      level: "info",
      msg: "otel enabled",
      endpoint: otel.endpoint,
      sample_ratio: otel.sampleRatio,
    });
  }

  const { default: PgBoss } = await import("pg-boss");
  const boss = new PgBoss({ connectionString: config.databaseUrl, schema: "pgboss" });
  boss.on("error", (error) => {
    writeLog({ level: "error", msg: "pg-boss", error: { message: String(error) } });
  });

  await boss.start();
  await boss.createQueue(DETECT_QUEUE);

  const api = createWorkerApi({ apiUrl: config.apiUrl, token: config.workerToken });
  const registry = new WorkerIndexRegistry({
    workspace: config.workspace,
    indexDir: config.indexDir,
  });
  await startWorkerIndexHttp(config, registry, api);

  await boss.work(DETECT_QUEUE, async (jobs) => {
    for (const job of jobs) {
      if (!isDetectJobData(job.data)) {
        observeJob(DETECT_QUEUE, "invalid", 0);
        throw new Error("invalid detect job payload");
      }
      const started = Date.now();
      try {
        await runDetectJob(job.data, { api, workspace: config.workspace });
        const repo = await api.getRepo(job.data.repo_id);
        const core = await registry.ensureIndexed(repo);
        if (core) {
          await api.reportIndex(repo.id, {
            last_indexed_at: core.lastIndexedAt()?.toISOString() ?? new Date().toISOString(),
          });
        }
        observeJob(DETECT_QUEUE, "ok", Date.now() - started);
        writeLog({
          level: "info",
          msg: "job",
          route: DETECT_QUEUE,
          project_id: job.data.project_id,
          duration_ms: Date.now() - started,
        });
      } catch (error) {
        observeJob(DETECT_QUEUE, "error", Date.now() - started);
        writeLog({
          level: "error",
          msg: "job",
          route: DETECT_QUEUE,
          project_id: job.data.project_id,
          duration_ms: Date.now() - started,
          error: { message: error instanceof Error ? error.message : String(error) },
        });
        throw error;
      }
    }
  });

  writeLog({
    level: "info",
    msg: "worker listening",
    queue: DETECT_QUEUE,
    index_rpc: `${config.indexRpcHost}:${config.indexRpcPort}`,
  });
}

const launchedDirectly =
  Boolean(process.argv[1]) && import.meta.url === pathToFileURL(process.argv[1]!).href;
if (launchedDirectly) {
  void main().catch((error: unknown) => {
    console.error(error);
    process.exit(1);
  });
}

export async function startWorker(): Promise<PgBoss> {
  const env = loadWorkerEnv();
  if (!env.databaseUrl) {
    throw new Error("DATABASE_URL is required");
  }
  if (!env.api.token) {
    throw new Error("BEACON_WORKER_TOKEN is required");
  }

  const api = createWorkerApi(env.api);
  const boss = new PgBoss({ connectionString: env.databaseUrl, schema: "pgboss" });
  boss.on("error", (error: unknown) => {
    console.error(
      JSON.stringify({
        level: "error",
        msg: "pg-boss error",
        error: error instanceof Error ? error.message : "unknown",
      }),
    );
  });
  await boss.start();
  await boss.createQueue(RETENTION_QUEUE);
  await boss.createQueue(EXPIRE_LOCKS_QUEUE);
  await boss.schedule(RETENTION_QUEUE, RETENTION_CRON);
  await boss.schedule(EXPIRE_LOCKS_QUEUE, EXPIRE_LOCKS_CRON);

  await boss.work(RETENTION_QUEUE, async () => {
    const result = await runRetentionJob(api);
    console.log(JSON.stringify({ level: "info", msg: "retention", ...result }));
  });
  await boss.work(EXPIRE_LOCKS_QUEUE, async () => {
    const result = await runExpireLocksJob(api);
    console.log(JSON.stringify({ level: "info", msg: "expire-locks", ...result }));
  });

  return boss;
}

const isMain = (() => {
  const entry = process.argv[1];
  if (!entry) {
    return false;
  }
  return /[/\\]apps[/\\]worker[/\\]src[/\\]index\.[cm]?[jt]s$/.test(entry);
})();

if (isMain) {
  startWorker().then(
    () => {
      console.log(JSON.stringify({ level: "info", msg: "worker started" }));
    },
    (error: unknown) => {
      console.error(
        JSON.stringify({
          level: "error",
          msg: "worker failed",
          error: error instanceof Error ? error.message : "unknown",
        }),
      );
      process.exit(1);
    },
  );
}
