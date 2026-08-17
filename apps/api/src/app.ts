import { Hono } from "hono";
import { checkDatabase } from "./db.js";

export const packageName = "@beacon/api";

export type ReadyCheck = () => Promise<boolean>;

export function createApp(options: { checkReady?: ReadyCheck } = {}): Hono {
  const checkReady = options.checkReady ?? (() => checkDatabase(process.env.DATABASE_URL));
  const app = new Hono();

  app.get("/health", (c) => c.json({ status: "ok" }));

  app.get("/ready", async (c) => {
    try {
      const ok = await checkReady();
      if (ok) {
        return c.json({ status: "ok" });
      }
    } catch {
      // ignore
    }
    return c.json({ status: "unavailable" }, 503);
  });

  return app;
}
