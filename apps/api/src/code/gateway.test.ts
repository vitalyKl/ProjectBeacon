import { describe, expect, it } from "vitest";

import { createCodeGateway, createIndexRpcClient } from "./gateway.js";
import type { ProjectRepoRecord } from "../context/types.js";

const REPO: ProjectRepoRecord = {
  id: "01934567-89ab-7cde-89ab-0123456789aa",
  projectId: "01934567-89ab-7cde-89ab-0123456789ab",
  provider: "local",
  remoteUrl: null,
  defaultBranch: "main",
  githubRepoId: null,
  installationId: null,
  localRootHint: "demo",
  indexMode: "bind_mount",
  lastIndexedSha: null,
  lastIndexedAt: null,
};

describe("CodeGateway", () => {
  it("calls worker loopback HTTP with the shared token", async () => {
    const calls: string[] = [];
    const fetchImpl = (async (input: string | URL | Request, init?: RequestInit) => {
      const url = String(input);
      calls.push(`${init?.method ?? "GET"} ${url}`);
      const headers = new Headers(init?.headers);
      expect(headers.get("authorization")).toBe("Bearer rpc-secret");
      return Response.json({ items: [] });
    }) as typeof fetch;
    const gateway = createCodeGateway({
      config: { indexRpcUrl: "http://worker:7744", indexRpcToken: "rpc-secret" },
      store: { findSidecarConnectionByRepoId: async () => undefined },
      now: () => new Date("2026-01-01T00:00:00.000Z"),
      fetchImpl,
    });
    await gateway.query(REPO, { kind: "tree", depth: 2 });
    expect(calls[0]).toContain("http://worker:7744/repos/");
    expect(calls[0]).toContain("/tree");
  });

  it("returns 503 when sidecar-only repos have no worker index", async () => {
    const gateway = createCodeGateway({
      config: { indexRpcUrl: "http://worker:7744", indexRpcToken: "rpc-secret" },
      store: { findSidecarConnectionByRepoId: async () => undefined },
      now: () => new Date("2026-01-01T00:00:00.000Z"),
      fetchImpl: (async () => {
        throw new Error("should not fetch");
      }) as typeof fetch,
    });
    await expect(
      gateway.query({ ...REPO, indexMode: "sidecar" }, { kind: "tree" }),
    ).rejects.toMatchObject({
      status: 503,
      code: "code_index_unavailable",
    });
  });

  it("prefers a recently seen sidecar in both mode and does not use worker HTTP", async () => {
    const gateway = createCodeGateway({
      config: { indexRpcUrl: "http://worker:7744", indexRpcToken: "rpc-secret" },
      store: {
        findSidecarConnectionByRepoId: async () => ({
          id: "sid-1",
          repoId: REPO.id,
          tokenId: "tok-1",
          connectedAt: new Date("2026-01-01T00:00:00.000Z"),
          lastSeenAt: new Date("2026-01-01T00:00:30.000Z"),
        }),
      },
      now: () => new Date("2026-01-01T00:00:40.000Z"),
      fetchImpl: (async () => {
        throw new Error("should not fetch");
      }) as typeof fetch,
    });
    await expect(
      gateway.query({ ...REPO, indexMode: "both" }, { kind: "tree" }),
    ).rejects.toMatchObject({
      status: 503,
      code: "code_index_unavailable",
    });
  });
});

describe("createIndexRpcClient", () => {
  it("surfaces worker 415 as unsupported_media", async () => {
    const client = createIndexRpcClient({
      baseUrl: "http://worker:7744",
      token: "rpc-secret",
      fetchImpl: (async () =>
        Response.json(
          { error: { code: "unsupported_media", message: "binary file" } },
          { status: 415 },
        )) as typeof fetch,
    });
    await expect(client.request(REPO.id, "/files", { path: "a.bin" })).rejects.toMatchObject({
      status: 415,
      code: "unsupported_media",
    });
  });
});
