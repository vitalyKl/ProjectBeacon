import { pathToFileURL } from "node:url";

import { createWorkerApi } from "./client.js";
import { loadWorkerConfig } from "./config.js";
import { runDetectJob } from "./detect.js";
import { DETECT_QUEUE, isDetectJobData } from "./jobs.js";

export const packageName = "@beacon/worker";

async function main(): Promise<void> {
  const config = loadWorkerConfig();
  if (!config.databaseUrl) {
    throw new Error("DATABASE_URL is required");
  }
  if (!config.workerToken) {
    throw new Error("BEACON_WORKER_TOKEN is required");
  }

  const { default: PgBoss } = await import("pg-boss");
  const boss = new PgBoss({ connectionString: config.databaseUrl, schema: "pgboss" });
  boss.on("error", (error) => {
    console.error(JSON.stringify({ level: "error", msg: "pg-boss", error: String(error) }));
  });

  await boss.start();
  await boss.createQueue(DETECT_QUEUE);

  const api = createWorkerApi({ apiUrl: config.apiUrl, token: config.workerToken });

  await boss.work(DETECT_QUEUE, async (jobs) => {
    for (const job of jobs) {
      if (!isDetectJobData(job.data)) {
        throw new Error("invalid detect job payload");
      }
      await runDetectJob(job.data, { api, workspace: config.workspace });
    }
  });

  console.log(JSON.stringify({ level: "info", msg: "worker listening", queue: DETECT_QUEUE }));
}

const launchedDirectly =
  Boolean(process.argv[1]) && import.meta.url === pathToFileURL(process.argv[1]!).href;
if (launchedDirectly) {
  void main().catch((error: unknown) => {
    console.error(error);
    process.exit(1);
  });
}
