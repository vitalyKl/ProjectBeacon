import { describe, expect, it } from "vitest";

import type { PublicRepo } from "./api";
import { projectRepoCatalog, repoPickerCatalogState, repoPickerOptions } from "./repo-picker";

function repo(overrides: Partial<PublicRepo> = {}): PublicRepo {
  return {
    id: "repo-1",
    project_id: "proj-1",
    index_mode: "sidecar",
    last_indexed_at: null,
    remote_url: "https://github.com/acme/app",
    local_root_hint: "apps/web",
    sidecar_connected: false,
    worker_index_connected: false,
    ...overrides,
  };
}

describe("repoPickerOptions", () => {
  it("maps project repos to select options and prefers the remote url label", () => {
    const listed = [
      repo({ id: "repo-1", remote_url: "https://github.com/acme/app" }),
      repo({
        id: "repo-2",
        remote_url: null,
        local_root_hint: "apps/api",
      }),
    ];
    expect(repoPickerOptions(listed)).toEqual([
      { id: "repo-1", label: "https://github.com/acme/app" },
      { id: "repo-2", label: "apps/api" },
    ]);
  });

  it("returns no options when the project has no repos", () => {
    expect(repoPickerOptions(null)).toEqual([]);
    expect(repoPickerOptions(undefined)).toEqual([]);
    expect(repoPickerOptions([])).toEqual([]);
  });

  it("does not treat a failed load as an empty catalog", () => {
    expect(repoPickerCatalogState({ repos: null, failed: false })).toBe("loading");
    expect(repoPickerCatalogState({ repos: [], failed: false })).toBe("empty");
    expect(repoPickerCatalogState({ repos: [repo()], failed: false })).toBe("ready");
    expect(repoPickerCatalogState({ repos: null, failed: true })).toBe("error");
    expect(repoPickerCatalogState({ repos: [], failed: true })).toBe("error");
  });

  it("treats a project switch as loading and drops a foreign selected id", () => {
    const listed = [repo({ id: "repo-1" })];
    expect(
      projectRepoCatalog({
        projectId: "proj-2",
        loadedProjectId: "proj-1",
        repos: listed,
        failed: false,
        selectedId: "repo-1",
      }),
    ).toEqual({
      repos: null,
      failed: false,
      selectedId: "",
      state: "loading",
    });
    expect(
      projectRepoCatalog({
        projectId: "proj-1",
        loadedProjectId: "proj-1",
        repos: listed,
        failed: false,
        selectedId: "repo-1",
      }).state,
    ).toBe("ready");
    expect(
      projectRepoCatalog({
        projectId: "proj-1",
        loadedProjectId: "proj-1",
        repos: listed,
        failed: false,
        selectedId: "foreign-repo",
      }).selectedId,
    ).toBe("");
    expect(
      projectRepoCatalog({
        projectId: "proj-1",
        loadedProjectId: "proj-1",
        repos: null,
        failed: true,
        selectedId: "repo-1",
      }).state,
    ).toBe("error");
  });

  it("keeps a selected id that is not in the fetched list", () => {
    expect(repoPickerOptions([repo()], "missing-repo")).toEqual([
      { id: "repo-1", label: "https://github.com/acme/app" },
      { id: "missing-repo", label: "missing-repo" },
    ]);
    expect(repoPickerOptions([repo()], "repo-1")).toEqual([
      { id: "repo-1", label: "https://github.com/acme/app" },
    ]);
  });
});
