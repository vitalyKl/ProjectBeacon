import { serve } from "@hono/node-server";
import { createApp } from "./app.js";
import { DETECT_QUEUE, MemoryJobQueue, PgBossJobQueue, type JobQueue } from "./jobs/queue.js";

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
  return new PgBossJobQueue((name, data, options) =>
    options ? boss.send(name, data, options) : boss.send(name, data),
  );
}

const jobs = await resolveJobs();
const app = createApp({ jobs });

serve({ fetch: app.fetch, port, hostname }, (info) => {
  console.log(
    JSON.stringify({
      level: "info",
      msg: "listening",
      port: info.port,
      hostname,
    }),
  );
});
