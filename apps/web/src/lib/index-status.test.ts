import { afterEach, describe, expect, it, vi } from "vitest";

import type { PublicRepo } from "./api";
import {
  connectionLabel,
  fetchDetailedProjectRepos,
  formatIndexWhen,
  indexModeLabel,
  pickHomeIndexRepo,
  repoDisplayName,
} from "./index-status";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function repo(overrides: Partial<PublicRepo> = {}): PublicRepo {
  return {
    id: "repo-1",
    project_id: "proj-1",
    index_mode: "sidecar",
    last_indexed_at: null,
    remote_url: null,
    local_root_hint: ".",
    sidecar_connected: false,
    worker_index_connected: false,
    ...overrides,
  };
}

describe("pickHomeIndexRepo", () => {
  it("returns null when the project has no repositories", () => {
    expect(pickHomeIndexRepo([], "repo-1")).toBeNull();
    expect(pickHomeIndexRepo(null, "repo-1")).toBeNull();
  });

  it("prefers the project's default repo when it is still listed", () => {
    const first = repo({ id: "repo-1", local_root_hint: "apps/web" });
    const preferred = repo({ id: "repo-2", local_root_hint: "apps/api" });
    expect(pickHomeIndexRepo([first, preferred], preferred.id)).toEqual(preferred);
  });

  it("falls back to the first listed repo when the default is gone", () => {
    const first = repo({ id: "repo-1" });
    expect(pickHomeIndexRepo([first], "missing")).toEqual(first);
  });
});

describe("index labels", () => {
  it("renders mode, connection, and missing timestamps", () => {
    expect(indexModeLabel(undefined)).toBe("Not connected");
    expect(indexModeLabel("bind_mount")).toBe("bind mount");
    expect(connectionLabel(true)).toBe("connected");
    expect(connectionLabel(false)).toBe("offline");
    expect(formatIndexWhen(null)).toBe("never");
    expect(formatIndexWhen("not-a-date")).toBe("not-a-date");
    expect(repoDisplayName({ remote_url: null, local_root_hint: null })).toBe("Repository");
    expect(repoDisplayName({ remote_url: "https://github.com/acme/app", local_root_hint: "." })).toBe(
      "https://github.com/acme/app",
    );
  });

  it("hides hosted-clone names when the flag is off", () => {
    expect(indexModeLabel("hosted_clone")).toBe("Not connected");
    expect(indexModeLabel("hosted_clone", { hostedClone: false })).toBe("Not connected");
    expect(indexModeLabel("hosted_clone", { hostedClone: true })).toBe("hosted clone");
    expect(indexModeLabel("both")).toBe("sidecar");
    expect(indexModeLabel("both", { hostedClone: false })).toBe("sidecar");
    expect(indexModeLabel("both", { hostedClone: true })).toBe("both");
    expect(indexModeLabel("sidecar", { hostedClone: false })).toBe("sidecar");
  });
});

describe("fetchDetailedProjectRepos", () => {
  it("hydrates each listed repo from GET /v1/repos/:id", async () => {
    const listed = repo({ id: "repo-1", sidecar_connected: false });
    const live = repo({
      id: "repo-1",
      sidecar_connected: true,
      last_indexed_at: "2026-08-18T12:00:00.000Z",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [listed], next_cursor: null }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(live), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(fetchDetailedProjectRepos("proj-1")).resolves.toEqual([live]);
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/projects/proj-1/repos?limit=100",
      expect.objectContaining({ credentials: "same-origin" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/v1/repos/repo-1",
      expect.objectContaining({ credentials: "same-origin" }),
    );
  });
});
