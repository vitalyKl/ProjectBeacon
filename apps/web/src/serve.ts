import { serve } from "@hono/node-server";
import { Hono } from "hono";

const mcpOrigin = (process.env.MCP_URL ?? "http://mcp:8080").replace(/\/+$/, "");
const port = Number.parseInt(process.env.PORT ?? "3000", 10);
const hostname = process.env.HOST ?? "0.0.0.0";

const app = new Hono();

app.get("/health", (c) => c.json({ status: "ok" }));

app.all("/mcp", async (c) => {
  const target = new URL("/mcp", `${mcpOrigin}/`);
  const incoming = new URL(c.req.url);
  target.search = incoming.search;
  const headers = new Headers(c.req.raw.headers);
  headers.delete("host");
  const init: RequestInit = {
    method: c.req.method,
    headers,
    redirect: "manual",
  };
  if (c.req.method !== "GET" && c.req.method !== "HEAD") {
    init.body = await c.req.raw.arrayBuffer();
  }
  const response = await fetch(target, init);
  return new Response(response.body, {
    status: response.status,
    headers: response.headers,
  });
});

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
