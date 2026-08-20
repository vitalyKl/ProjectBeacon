import { pathToFileURL } from "node:url";

import { endSpan, loadOtelConfig, observeJob, startSpan, writeLog } from "@beacon/shared";

import { createWorkerApi as createHygieneApi, loadWorkerEnv } from "./api.js";
import { createWorkerApi } from "./client.js";
import { loadWorkerConfig } from "./config.js";
import { runDetectJob } from "./detect.js";
import { runGithubImportJob, runGithubInvalidateJob } from "./github.js";
import { startWorkerIndexHttp, WorkerIndexRegistry } from "./index-server.js";
import {
  DETECT_QUEUE,
  EXPIRE_LOCKS_CRON,
  EXPIRE_LOCKS_QUEUE,
  GITHUB_IMPORT_QUEUE,
  GITHUB_INVALIDATE_QUEUE,
  RETENTION_CRON,
  RETENTION_QUEUE,
  isDetectJobData,
  isGithubImportJobData,
  isGithubInvalidateJobData,
  runExpireLocksJob,
  runRetentionJob,
} from "./jobs.js";
import { purgeDeletedProjectClones } from "./purge.js";

export const packageName = "@beacon/worker";
export { createWorkerApi as createHygieneApi, loadWorkerEnv } from "./api.js";
export {
  EXPIRE_LOCKS_CRON,
  EXPIRE_LOCKS_QUEUE,
  RETENTION_CRON,
  RETENTION_QUEUE,
  runExpireLocksJob,
  runRetentionJob,
} from "./jobs.js";

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
  await boss.createQueue(GITHUB_IMPORT_QUEUE);
  await boss.createQueue(GITHUB_INVALIDATE_QUEUE);
  await boss.createQueue(RETENTION_QUEUE);
  await boss.createQueue(EXPIRE_LOCKS_QUEUE);
  await boss.schedule(RETENTION_QUEUE, RETENTION_CRON);
  await boss.schedule(EXPIRE_LOCKS_QUEUE, EXPIRE_LOCKS_CRON);

  const api = createWorkerApi({ apiUrl: config.apiUrl, token: config.workerToken });
  const hygiene = createHygieneApi({
    baseUrl: config.apiUrl,
    token: config.workerToken,
  });
  const registry = new WorkerIndexRegistry({
    workspace: config.workspace,
    indexDir: config.indexDir,
    cloneDir: config.cloneDir,
    githubApp: config.githubApp,
  });
  await startWorkerIndexHttp(config, registry, api);

  await boss.work(DETECT_QUEUE, async (jobs) => {
    for (const job of jobs) {
      if (!isDetectJobData(job.data)) {
        observeJob(DETECT_QUEUE, "invalid", 0);
        throw new Error("invalid detect job payload");
      }
      const started = Date.now();
      const span = startSpan("job.detect", {
        config: otel,
        attributes: { queue: DETECT_QUEUE, project_id: job.data.project_id },
      });
      try {
        await runDetectJob(job.data, { api, workspace: config.workspace });
        const repo = await api.getRepo(job.data.repo_id);
        let forceClone = false;
        if (repo.index_mode === "hosted_clone" || repo.index_mode === "both") {
          const pending = await api.consumeCloneInvalidation(repo.id);
          forceClone = Boolean(pending.consumed);
        }
        const core = await registry.ensureIndexed(repo, { forceClone });
        if (core) {
          await api.reportIndex(repo.id, {
            last_indexed_at: core.lastIndexedAt()?.toISOString() ?? new Date().toISOString(),
          });
        }
        observeJob(DETECT_QUEUE, "ok", Date.now() - started);
        endSpan(span, "ok");
        writeLog({
          level: "info",
          msg: "job",
          route: DETECT_QUEUE,
          project_id: job.data.project_id,
          duration_ms: Date.now() - started,
        });
      } catch (error) {
        observeJob(DETECT_QUEUE, "error", Date.now() - started);
        endSpan(span, "error");
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

  await boss.work(GITHUB_IMPORT_QUEUE, async (jobs) => {
    for (const job of jobs) {
      if (!isGithubImportJobData(job.data)) {
        throw new Error("invalid github import job payload");
      }
      await runGithubImportJob(job.data, { api });
    }
  });
  await boss.work(GITHUB_INVALIDATE_QUEUE, async (jobs) => {
    for (const job of jobs) {
      if (!isGithubInvalidateJobData(job.data)) {
        throw new Error("invalid github invalidate job payload");
      }
      await runGithubInvalidateJob(job.data, { api });
    }
  });
  await boss.work(RETENTION_QUEUE, async () => {
    const result = await runRetentionJob(hygiene);
    writeLog({ level: "info", msg: "retention", ...result });
  });
  await boss.work(EXPIRE_LOCKS_QUEUE, async () => {
    const result = await runExpireLocksJob(hygiene);
    writeLog({ level: "info", msg: "expire-locks", ...result });
  });

  const sweep = async () => {
    try {
      await purgeDeletedProjectClones(api, registry);
    } catch (error) {
      writeLog({
        level: "error",
        msg: "deleted project index purge",
        error: { message: error instanceof Error ? error.message : String(error) },
      });
    }
  };
  await sweep();
  setInterval(() => {
    void sweep();
  }, 30_000);

  writeLog({
    level: "info",
    msg: "worker listening",
    queues: [DETECT_QUEUE, GITHUB_IMPORT_QUEUE, GITHUB_INVALIDATE_QUEUE],
    index_rpc: `${config.indexRpcHost}:${config.indexRpcPort}`,
  });
}

export async function startWorker() {
  const env = loadWorkerEnv();
  if (!env.databaseUrl) {
    throw new Error("DATABASE_URL is required");
  }
  if (!env.api.token) {
    throw new Error("BEACON_WORKER_TOKEN is required");
  }
  await main();
}

const launchedDirectly =
  Boolean(process.argv[1]) && import.meta.url === pathToFileURL(process.argv[1]!).href;
if (launchedDirectly) {
  void main().catch((error: unknown) => {
    console.error(error);
    process.exit(1);
  });
}
