import { describe, expect, it } from "vitest";

import { createWorkerApi, WorkerApiError } from "./client.js";

describe("worker /v1 client", () => {
  it("sends bearer and parse repo, import, milestone, and task calls", async () => {
    const calls: Array<{ url: string; method: string; headers: Headers; body: string | null }> = [];
    const fetchImpl: typeof fetch = async (input, init) => {
      const url =
        typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
      calls.push({
        url,
        method: init?.method ?? "GET",
        headers: new Headers(init?.headers),
        body: typeof init?.body === "string" ? init.body : null,
      });
      if (url.endsWith("/v1/repos/repo-1")) {
        return Response.json({
          id: "repo-1",
          project_id: "proj-1",
          provider: "local",
          local_root_hint: "demo",
          index_mode: "bind_mount",
        });
      }
      if (url.includes("/context/import")) {
        return Response.json({ nodes: [], code_owners_written: 0 });
      }
      if (url.endsWith("/milestones") && init?.method === "GET") {
        return Response.json({ items: [{ id: "ms-1", title: "Detector skeleton" }] });
      }
      if (url.endsWith("/milestones")) {
        return Response.json({ id: "ms-1" }, { status: 201 });
      }
      if (url.endsWith("/tasks") && init?.method === "GET") {
        return Response.json({ items: [] });
      }
      if (url.endsWith("/tasks")) {
        return Response.json({ id: "task-1" }, { status: 201 });
      }
      return new Response("missing", { status: 404 });
    };

    const api = createWorkerApi({
      apiUrl: "http://api:8080",
      token: "worker-secret",
      fetchImpl,
    });

    await expect(api.getRepo("repo-1")).resolves.toMatchObject({
      id: "repo-1",
      project_id: "proj-1",
    });
    await api.importContext("proj-1", "repo-1", [{ path: "AGENTS.md", content: "# hi" }]);
    await expect(api.listMilestones("proj-1")).resolves.toEqual([
      { id: "ms-1", title: "Detector skeleton" },
    ]);
    await api.createMilestone("proj-1", { title: "Detector skeleton" });
    await expect(api.listTasks("proj-1")).resolves.toEqual([]);
    await api.createTask(
      "proj-1",
      { title: "Review imported project context" },
      "detect:repo-1:task:0",
    );

    expect(calls[0]?.headers.get("authorization")).toBe("Bearer worker-secret");
    expect(calls[1]?.url).toContain("/v1/projects/proj-1/context/import?repo_id=repo-1");
    expect(calls.at(-1)?.headers.get("idempotency-key")).toBe("detect:repo-1:task:0");
  });

  it("throws WorkerApiError on non-2xx", async () => {
    const api = createWorkerApi({
      apiUrl: "http://api:8080",
      token: "worker-secret",
      fetchImpl: async () =>
        Response.json({ error: { code: "not_found", message: "repo not found" } }, { status: 404 }),
    });
    await expect(api.getRepo("missing")).rejects.toMatchObject({
      name: "WorkerApiError",
      status: 404,
      code: "not_found",
    } satisfies Partial<WorkerApiError>);
  });
});
