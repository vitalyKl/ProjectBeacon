import { createDb } from "@beacon/db";
import { Hono } from "hono";
import { type AuthConfig, loadAuthConfig } from "./auth/config.js";
import { systemClock, type Clock } from "./auth/clock.js";
import { DbAuthStore } from "./auth/db-store.js";
import { DEFAULT_RATE_LIMITS, type RateLimitConfig } from "./auth/rate-limit.js";
import { mountAuth } from "./auth/routes.js";
import { MemoryAuthStore, type AuthStore } from "./auth/store.js";
import { mountContext } from "./context/routes.js";
import { checkDatabase } from "./db.js";
import { MemoryJobQueue, type JobQueue } from "./jobs/queue.js";
import { mountOrgs } from "./orgs/routes.js";
import { mountRepos } from "./repos/routes.js";
import { mountRoadmap } from "./roadmap/routes.js";
import { mountSessions } from "./sessions/routes.js";
import { mountTokenProbe, mountTokens } from "./tokens/routes.js";
import { mountDecisions } from "./context/decisions.js";
import { mountJobs } from "./jobs/routes.js";
import { mountGithub } from "./github/routes.js";
import { createCodeGateway, type CodeGateway } from "./code/gateway.js";
import { isSidecarTunnelEnabled } from "./code/flags.js";
import { createSidecarTunnelHub, type SidecarTunnelHub } from "./code/tunnel.js";
import { errorJson } from "./errors.js";

export const packageName = "@beacon/api";

export type ReadyCheck = () => Promise<boolean>;

export type CreateAppOptions = {

  checkReady?: ReadyCheck;
  store?: AuthStore;
  config?: AuthConfig;
  clock?: Clock;
  githubFetch?: typeof fetch;
  databaseUrl?: string;
  rateLimits?: RateLimitConfig;
  enableTokenProbe?: boolean;
  jobs?: JobQueue;
  codeGateway?: CodeGateway;
  sidecarTunnel?: SidecarTunnelHub;
  sidecarTunnelEnabled?: () => boolean;
};

function resolveStore(options: CreateAppOptions): AuthStore {
  if (options.store) {
    return options.store;
  }
  const databaseUrl = options.databaseUrl ?? process.env.DATABASE_URL;
  if (databaseUrl) {
    return new DbAuthStore(createDb(databaseUrl));
  }
  return new MemoryAuthStore();
}

export function createApp(options: CreateAppOptions = {}): CreatedApp {
  const checkReady = options.checkReady ?? (() => checkDatabase(process.env.DATABASE_URL));
  const app = new Hono() as CreatedApp;
  const sidecarTunnel = options.sidecarTunnel ?? createSidecarTunnelHub();
  const sidecarTunnelEnabled = options.sidecarTunnelEnabled ?? (() => isSidecarTunnelEnabled());

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

  const authDeps = {
    store: resolveStore(options),
    config: options.config ?? loadAuthConfig(),
    clock: options.clock ?? systemClock,
    githubFetch: options.githubFetch ?? fetch,
    rateLimits: options.rateLimits ?? DEFAULT_RATE_LIMITS,
  };
  const codeGateway =
    options.codeGateway ??
    createCodeGateway({
      config: authDeps.config,
      store: authDeps.store,
      now: () => authDeps.clock.now(),
      tunnel: sidecarTunnel,
      tunnelEnabled: sidecarTunnelEnabled,
    });
  mountAuth(app, authDeps);
  mountOrgs(app, authDeps);
  mountRoadmap(app, authDeps);
  mountContext(app, { ...authDeps, codeGateway });
  mountDecisions(app, authDeps);
  mountTokens(app, authDeps);
  mountSessions(app, { ...authDeps, codeGateway });
  mountRepos(app, { ...authDeps, jobs: options.jobs ?? new MemoryJobQueue(), codeGateway });
  if (options.enableTokenProbe) {
    mountTokenProbe(app, authDeps);
  }

  app.get("/v1/sidecar", (c) => {
    if (!sidecarTunnelEnabled()) {
      return errorJson(c, 404, "not_found", "not found");
    }
    return errorJson(c, 400, "unauthorized", "websocket upgrade required");
  });

  app.sidecarTunnel = sidecarTunnel;
  app.sidecarTunnelEnabled = sidecarTunnelEnabled;
  app.authDeps = authDeps;
  return app;
}

export type CreatedApp = Hono & {
  sidecarTunnel: SidecarTunnelHub;
  sidecarTunnelEnabled: () => boolean;
  authDeps: {
    store: AuthStore;
    config: AuthConfig;
    clock: Clock;
    githubFetch: typeof fetch;
    rateLimits: RateLimitConfig;
  };
};
