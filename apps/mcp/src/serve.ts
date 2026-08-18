import { serve } from "@hono/node-server";

import { createApp } from "./app.js";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const hostname = process.env.HOST ?? "0.0.0.0";

const app = createApp();

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
