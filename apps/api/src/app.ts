import { createDb } from "@beacon/db";
import { Hono } from "hono";

import { type AuthConfig, loadAuthConfig } from "./auth/config.js";
import { systemClock, type Clock } from "./auth/clock.js";
import { DbAuthStore } from "./auth/db-store.js";
import { DEFAULT_RATE_LIMITS, type RateLimitConfig } from "./auth/rate-limit.js";
import { mountAuth } from "./auth/routes.js";
import type { IdentityStore } from "./auth/identity.js";
import { MemoryAuthStore } from "./auth/store.js";
import type { ContextStore } from "./context/store.js";
import type { GithubStore } from "./github/store.js";
import type { JobStore } from "./jobs/store.js";
import type { OrgStore } from "./orgs/store.js";
import type { ReportStore } from "./reports/store.js";
import type { RepoStore } from "./repos/store.js";
import type { RoadmapStore } from "./roadmap/store.js";
import type { WorkStore } from "./sessions/store.js";
import type { TokenStore } from "./tokens/store.js";
import { isSidecarTunnelEnabled } from "./code/flags.js";
import { createCodeGateway, type CodeGateway } from "./code/gateway.js";
import { createSidecarTunnelHub, type SidecarTunnelHub } from "./code/tunnel.js";
import { mountDecisions } from "./context/decisions.js";
import { mountContext } from "./context/routes.js";
import { checkDatabase } from "./db.js";
import { errorJson } from "./errors.js";
import { mountGithub } from "./github/routes.js";
import { MemoryJobQueue, type JobQueue } from "./jobs/queue.js";
import { mountJobs } from "./jobs/routes.js";
import { mountLabels } from "./labels/routes.js";
import { mountObservability } from "./observability.js";
import { mountOrgs } from "./orgs/routes.js";
import { mountReports } from "./reports/routes.js";
import { mountRepos } from "./repos/routes.js";
import { mountRoadmap } from "./roadmap/routes.js";
import { mountSessions } from "./sessions/routes.js";
import { mountTokenProbe, mountTokens } from "./tokens/routes.js";

export const packageName = "@beacon/api";

export type ReadyCheck = () => Promise<boolean>;

export type AppStore = IdentityStore &
  OrgStore &
  TokenStore &
  RoadmapStore &
  ContextStore &
  WorkStore &
  RepoStore &
  GithubStore &
  ReportStore &
  JobStore;

export type CreateAppOptions = {
  checkReady?: ReadyCheck;
  store?: AppStore;
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

export type CreatedApp = Hono & {
  sidecarTunnel: SidecarTunnelHub;
  sidecarTunnelEnabled: () => boolean;
  authDeps: {
    store: AppStore;
    config: AuthConfig;
    clock: Clock;
    githubFetch: typeof fetch;
    rateLimits: RateLimitConfig;
  };
};

function resolveStore(options: CreateAppOptions): AppStore {
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
  mountObservability(app);

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
  const jobs = options.jobs ?? new MemoryJobQueue();
  const codeGateway =
    options.codeGateway ??
    createCodeGateway({
      config: authDeps.config,
      tunnel: sidecarTunnel,
    });
  mountAuth(app, authDeps);
  mountOrgs(app, authDeps);
  mountRoadmap(app, authDeps);
  mountContext(app, { ...authDeps, codeGateway });
  mountDecisions(app, authDeps);
  mountLabels(app, authDeps);
  mountReports(app, authDeps);
  mountTokens(app, authDeps);
  mountSessions(app, authDeps);
  mountRepos(app, { ...authDeps, jobs, codeGateway });
  mountGithub(app, { ...authDeps, jobs });
  mountJobs(app, authDeps);
  if (options.enableTokenProbe) {
    mountTokenProbe(app, authDeps);
  }

  app.get("/v1/sidecar", (c) => {
    if (!sidecarTunnelEnabled()) {
      return errorJson(c, 404, "not_found", "not found");
    }
    return errorJson(c, 400, "invalid_request", "websocket upgrade required");
  });

  app.sidecarTunnel = sidecarTunnel;
  app.sidecarTunnelEnabled = sidecarTunnelEnabled;
  app.authDeps = authDeps;
  return app;
}
