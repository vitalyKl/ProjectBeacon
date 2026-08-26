import type { Context } from "hono";

import { isResponse } from "../http/parse.js";
import type { AccessDeps, AuthActor, ProjectAccess } from "./access.js";
import { requireActor, requireProject } from "./access.js";

/** Middleware that resolves the authenticated actor, finds a project
 *  scope from the request path, and sets `app.actor_org_id` on the DB
 *  store so that Postgres RLS is active for every query in the request.
 *
 *  The org context must be set before any route handler executes,
 *  because handlers call `requireProject` which runs DB queries
 *  directly against the store.
 *
 *  This is a best-effort guard: if resolution fails (no project in path,
 *  unauthenticated, or DB unavailable) the request proceeds without the
 *  session variable. App-layer authorisation still protects those paths.
 */
export function withRlsOrgContext(deps: AccessDeps) {
  return async (c: Context, next: () => Promise<void>) => {
    try {
      const actor: AuthActor | Response = await requireActor(c, deps);
      if (isResponse(actor)) {
        // Authentication failed — let the handler deal with the 401.
        return next();
      }

      const projectId = c.req.param("projectId");
      if (!projectId) {
        // No project scope (e.g. /v1/auth, /v1/me) — nothing to do.
        return next();
      }

      try {
        const access: ProjectAccess | Response = await requireProject(
          c,
          deps,
          projectId,
          "project:read",
        );
        if (!(access instanceof Response)) {
          deps.setOrgId(access.project.orgId);
        }
      } catch {
        // Project lookup failed — continue without RLS guard.
      }
    } catch {
      // Top-level resolution failed — continue without RLS guard.
    }

    return next();
  };
}
