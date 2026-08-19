import { describe, expect, it } from "vitest";

import { createApp } from "./app.js";
import type { AuthConfig } from "./auth/config.js";
import { MemoryAuthStore } from "./auth/store.js";

const BOOTSTRAP_TOKEN = "bootstrap-admin-token-for-tests";
const STRONG_PASSWORD = "correct-horse";

function testConfig(overrides: Partial<AuthConfig> = {}): AuthConfig {
  return {
    bootstrapAdminToken: BOOTSTRAP_TOKEN,
    workerToken: undefined,
    authLocal: true,
    authLocalInviteOnly: false,
    authGithub: false,
    githubClientId: undefined,
    githubClientSecret: undefined,
    secureCookies: false,
    trustProxy: false,
    indexRpcUrl: "http://127.0.0.1:7744",
    indexRpcToken: "index-rpc-test",
    ...overrides,
  };
}

function sessionCookie(res: Response): string | undefined {
  const header = res.headers.get("set-cookie");
  if (!header) {
    return undefined;
  }
  const match = /(?:^|,\s*)beacon_session=([^;]+)/.exec(header);
  return match?.[1];
}

function cookieHeader(token: string): string {
  return `beacon_session=${token}`;
}

async function registerUser(store: MemoryAuthStore, login: string) {
  const app = createApp({ store, config: testConfig(), checkReady: async () => true });
  const res = await app.request("/v1/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ login, password: STRONG_PASSWORD }),
  });
  const token = sessionCookie(res);
  const body = (await res.json()) as { id: string; login: string };
  return { app, token: token!, user: body };
}

async function createProject(app: ReturnType<typeof createApp>, token: string, slug: string) {
  const me = await app.request("/v1/me", { headers: { cookie: cookieHeader(token) } });
  const personal = ((await me.json()) as { personal_org: { id: string } }).personal_org;
  const created = await app.request(`/v1/orgs/${personal.id}/projects`, {
    method: "POST",
    headers: { cookie: cookieHeader(token), "content-type": "application/json" },
    body: JSON.stringify({ slug, name: slug }),
  });
  return (await created.json()) as { id: string; name: string };
}

describe("reports and reviews", () => {
  it("generates a snapshot report and imports a review agents can read", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "reports");

    const imported = await alice.app.request(`/v1/projects/${project.id}/reviews`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({
        body_md: "# Board review\nThe dependency graph labels wrap poorly.",
        source_path: "notes/board-review.md",
      }),
    });
    expect(imported.status).toBe(201);
    const review = (await imported.json()) as { id: string; title: string; status: string };
    expect(review.title).toBe("Board review");
    expect(review.status).toBe("needs_review");

    const listedReviews = await alice.app.request(`/v1/projects/${project.id}/reviews`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listedReviews.status).toBe(200);
    expect(await listedReviews.json()).toMatchObject({
      items: [{ id: review.id, title: "Board review" }],
    });

    const created = await alice.app.request(`/v1/projects/${project.id}/reports`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(created.status).toBe(201);
    const report = (await created.json()) as {
      title: string;
      body_md: string;
      snapshot: { review_ids: string[]; tasks: Record<string, number> };
    };
    expect(report.body_md).toContain("development report");
    expect(report.snapshot.review_ids).toEqual([review.id]);
    expect(report.snapshot.tasks["backlog"]).toBeGreaterThanOrEqual(0);

    const listed = await alice.app.request(`/v1/projects/${project.id}/reports`, {
      headers: { cookie: cookieHeader(alice.token) },
    });
    expect(listed.status).toBe(200);
    const page = (await listed.json()) as { items: { body_md: string }[] };
    expect(page.items[0]?.body_md).toContain("Board review");
  });

  it("maps a missing reports table to a migrate hint instead of a raw 500", async () => {
    const store = new MemoryAuthStore();
    store.listReviews = async () => {
      throw Object.assign(new Error('relation "project_reviews" does not exist'), { code: "42P01" });
    };
    const alice = await registerUser(store, "alice-schema");
    const project = await createProject(alice.app, alice.token, "reports-schema");
    const created = await alice.app.request(`/v1/projects/${project.id}/reports`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(created.status).toBe(503);
    expect(await created.json()).toMatchObject({
      error: {
        code: "integration_unavailable",
        details: { reason: "missing_schema" },
      },
    });
  });

  it("stores how_to_check on create and finish_work", async () => {
    const store = new MemoryAuthStore();
    const alice = await registerUser(store, "alice");
    const project = await createProject(alice.app, alice.token, "check");
    const created = await alice.app.request(`/v1/projects/${project.id}/tasks`, {
      method: "POST",
      headers: {
        cookie: cookieHeader(alice.token),
        "content-type": "application/json",
        "idempotency-key": "create-check",
      },
      body: JSON.stringify({
        title: "Add how to check",
        how_to_check: "Open the task and read How to check.",
      }),
    });
    expect(created.status).toBe(201);
    const task = (await created.json()) as { id: string; how_to_check: string; version: number };
    expect(task.how_to_check).toBe("Open the task and read How to check.");

    const tokenRes = await alice.app.request(`/v1/projects/${project.id}/tokens`, {
      method: "POST",
      headers: { cookie: cookieHeader(alice.token), "content-type": "application/json" },
      body: JSON.stringify({ name: "agent" }),
    });
    const secret = ((await tokenRes.json()) as { token: string }).token;
    const started = await alice.app.request(`/v1/projects/${project.id}/sessions`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${secret}`,
        "content-type": "application/json",
        "idempotency-key": "start-check",
      },
      body: JSON.stringify({ task_id: task.id }),
    });
    const session = ((await started.json()) as { session: { id: string } }).session;
    const finished = await alice.app.request(`/v1/sessions/${session.id}/finish`, {
      method: "POST",
      headers: { authorization: `Bearer ${secret}`, "content-type": "application/json" },
      body: JSON.stringify({
        summary: "Wrote how to check notes for the human reviewer.",
        how_to_check: "Open /app/tasks and confirm the notes render.",
      }),
    });
    expect(finished.status).toBe(200);
    const after = await store.findTaskById(task.id);
    expect(after?.howToCheck).toBe("Open /app/tasks and confirm the notes render.");
  });
});
