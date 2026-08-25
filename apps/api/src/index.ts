import { serve } from "@hono/node-server";
import { loadOtelConfig, writeLog } from "@beacon/shared";

import { createApp } from "./app.js";
import { attachSidecarTunnelUpgrade } from "./code/routes.js";
import {
  DETECT_QUEUE,
  GITHUB_IMPORT_QUEUE,
  GITHUB_INVALIDATE_QUEUE,
  WEBHOOK_DELIVERY_QUEUE,
  MemoryJobQueue,
  PgBossJobQueue,
  type JobQueue,
} from "./jobs/queue.js";
import { refreshObservabilityGauges } from "./observability.js";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const hostname = process.env.HOST ?? "0.0.0.0";

async function resolveJobs(): Promise<JobQueue> {
  const databaseUrl = process.env.DATABASE_URL;
  if (!databaseUrl) {
    return new MemoryJobQueue();
  }
  const { default: PgBoss } = await import("pg-boss");
  const boss = new PgBoss({ connectionString: databaseUrl, schema: "pgboss" });
  boss.on("error", (error) => {
    console.error(JSON.stringify({ level: "error", msg: "pg-boss", error: String(error) }));
  });
  await boss.start();
  await boss.createQueue(DETECT_QUEUE);
  await boss.createQueue(GITHUB_IMPORT_QUEUE);
  await boss.createQueue(GITHUB_INVALIDATE_QUEUE);
  await boss.createQueue(WEBHOOK_DELIVERY_QUEUE);
  return new PgBossJobQueue((name, data, options) =>
    options ? boss.send(name, data, options) : boss.send(name, data),
  );
}

const jobs = await resolveJobs();
const app = createApp({ jobs });
try {
  await app.authDeps.store.backfillEmptyProjectLabelCatalogs();
} catch (error) {
  writeLog({
    level: "error",
    msg: "starter label backfill failed",
    error: { message: error instanceof Error ? error.message : String(error) },
  });
}
const otel = loadOtelConfig(process.env, "beacon-api");
if (otel.enabled) {
  writeLog({
    level: "info",
    msg: "otel enabled",
    sample_ratio: otel.sampleRatio,
  });
}

const server = serve({ fetch: app.fetch, port, hostname }, (info) => {
  writeLog({
    level: "info",
    msg: "listening",
    port: info.port,
    hostname,
  });
});

attachSidecarTunnelUpgrade(server, app.authDeps, app.sidecarTunnel, app.sidecarTunnelEnabled);

const databaseUrl = process.env.DATABASE_URL;
if (databaseUrl) {
  const { createDb } = await import("@beacon/db");
  const { DbAuthStore } = await import("./auth/db-store.js");
  const gauges = new DbAuthStore(createDb(databaseUrl));
  const tick = () => {
    void refreshObservabilityGauges(gauges).catch(() => undefined);
  };
  tick();
  setInterval(tick, 30_000).unref();
}
