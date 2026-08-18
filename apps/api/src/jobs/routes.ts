import type { Context, Hono } from "hono";

import type { AuthDeps } from "../auth/routes.js";
import { parseBearer, tokenEquals } from "../auth/tokens.js";
import { errorJson } from "../errors.js";

function requireWorker(c: Context, deps: AuthDeps) {
  const expected = deps.config.workerToken;
  const provided = parseBearer(c.req.header("authorization"));
  if (!tokenEquals(expected, provided)) {
    return errorJson(c, 401, "unauthorized", "invalid token");
  }
  return null;
}

export function mountJobs(app: Hono, deps: AuthDeps): void {
  app.post("/v1/jobs/retention", async (c) => {
    const denied = requireWorker(c, deps);
    if (denied) {
      return denied;
    }
    const counts = await deps.store.runRetention(deps.clock.now());
    return c.json({
      activity_deleted: counts.activityDeleted,
      briefs_deleted: counts.briefsDeleted,
      idempotency_deleted: counts.idempotencyDeleted,
      sessions_deleted: counts.sessionsDeleted,
    });
  });

  app.post("/v1/jobs/expire-locks", async (c) => {
    const denied = requireWorker(c, deps);
    if (denied) {
      return denied;
    }
    const counts = await deps.store.expireLocks(deps.clock.now());
    return c.json({
      locks_released: counts.locksReleased,
      sessions_abandoned: counts.sessionsAbandoned,
    });
  });
}
