import { serve } from "@hono/node-server";
import { loadOtelConfig, writeLog } from "@beacon/shared";

import { createApp } from "./app.js";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const hostname = process.env.HOST ?? "0.0.0.0";

const app = createApp();
const otel = loadOtelConfig(process.env, "beacon-mcp");
if (otel.enabled) {
  writeLog({
    level: "info",
    msg: "otel enabled",
    endpoint: otel.endpoint,
    sample_ratio: otel.sampleRatio,
  });
}

serve({ fetch: app.fetch, port, hostname }, (info) => {
  writeLog({
    level: "info",
    msg: "listening",
    port: info.port,
    hostname,
  });
});
