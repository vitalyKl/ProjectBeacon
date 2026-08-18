import { describe, expect, it, vi } from "vitest";

import { createWorkerApi } from "./api.js";
import { runExpireLocksJob, runRetentionJob } from "./jobs.js";

describe("worker hygiene client", () => {
  it("POSTs retention and expire-locks to /v1 with the worker token", async () => {
    const fetchImpl = vi.fn(async (input: string | URL, init?: RequestInit) => {
      const url = String(input);
      expect(init?.method).toBe("POST");
      expect(init?.headers).toMatchObject({ authorization: "Bearer worker-token" });
      if (url.endsWith("/v1/jobs/retention")) {
        return new Response(
          JSON.stringify({
            activity_deleted: 1,
            briefs_deleted: 2,
            idempotency_deleted: 3,
            sessions_deleted: 4,
          }),
          { status: 200, headers: { "content-type": "application/json" } },
        );
      }
      if (url.endsWith("/v1/jobs/expire-locks")) {
        return new Response(JSON.stringify({ locks_released: 5, sessions_abandoned: 6 }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      }
      return new Response("missing", { status: 404 });
    });

    const api = createWorkerApi({
      baseUrl: "http://api.example",
      token: "worker-token",
      fetchImpl: fetchImpl as unknown as typeof fetch,
    });

    await expect(runRetentionJob(api)).resolves.toEqual({
      activity_deleted: 1,
      briefs_deleted: 2,
      idempotency_deleted: 3,
      sessions_deleted: 4,
    });
    await expect(runExpireLocksJob(api)).resolves.toEqual({
      locks_released: 5,
      sessions_abandoned: 6,
    });
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it("throws when the API rejects the worker token", async () => {
    const api = createWorkerApi({
      baseUrl: "http://api.example",
      token: "bad",
      fetchImpl: (async () => new Response("no", { status: 401 })) as typeof fetch,
    });
    await expect(runRetentionJob(api)).rejects.toThrow("/v1/jobs/retention failed: 401");
  });
});
