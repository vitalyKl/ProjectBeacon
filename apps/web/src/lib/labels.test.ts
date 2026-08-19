import { afterEach, describe, expect, it, vi } from "vitest";

import {
  activeCatalogLabels,
  createLabel,
  fetchProjectLabels,
  formatLabelPaths,
  patchLabel,
  removeLabelPath,
  setTaskLabels,
  sortLabels,
  toggleLabelId,
  upsertLabelPath,
  type PublicLabel,
} from "./labels";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function label(overrides: Partial<PublicLabel> = {}): PublicLabel {
  return {
    id: "lab-1",
    project_id: "proj-1",
    slug: "api",
    name: "API",
    description: "",
    color: "#3366ff",
    status: "active",
    created_at: "2026-08-18T12:00:00.000Z",
    paths: [{ repo_id: "repo-1", path: "apps/api" }],
    ...overrides,
  };
}

describe("label helpers", () => {
  it("sorts proposed first, then by name", () => {
    const visual = label({ id: "visual", slug: "visual", name: "Visual", status: "proposed" });
    const web = label({ id: "web", slug: "web", name: "Web", status: "active" });
    const api = label({ id: "api", slug: "api", name: "API", status: "active" });
    expect(sortLabels([web, visual, api]).map((item) => item.id)).toEqual(["visual", "api", "web"]);
  });

  it("toggles ids and formats empty path lists", () => {
    expect(toggleLabelId(["a"], "b")).toEqual(["a", "b"]);
    expect(toggleLabelId(["a", "b"], "a")).toEqual(["b"]);
    expect(formatLabelPaths(label({ paths: [] }))).toBe("No path prefixes");
    expect(activeCatalogLabels([label(), label({ status: "proposed" })])).toHaveLength(1);
    expect(
      upsertLabelPath([{ repo_id: "repo-1", path: "apps/api" }], {
        repo_id: "repo-1",
        path: "/apps/web/",
      }),
    ).toEqual([
      { repo_id: "repo-1", path: "apps/api" },
      { repo_id: "repo-1", path: "apps/web" },
    ]);
    expect(
      removeLabelPath(
        [
          { repo_id: "repo-1", path: "apps/api" },
          { repo_id: "repo-1", path: "apps/web" },
        ],
        "repo-1",
        "apps/api",
      ),
    ).toEqual([{ repo_id: "repo-1", path: "apps/web" }]);
  });
});

describe("labels API client", () => {
  it("lists the project catalog and attaches labels to a task", async () => {
    const listed = label({ id: "lab-a" });
    const created = label({ id: "lab-b", slug: "web", name: "Web" });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [listed], next_cursor: null }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(created), {
          status: 201,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ...created, paths: [{ repo_id: "repo-1", path: "apps/web" }] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            id: "task-1",
            labels: [{ id: listed.id, slug: listed.slug, name: listed.name }],
          }),
          {
            status: 200,
            headers: { "content-type": "application/json" },
          },
        ),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(fetchProjectLabels("proj-1")).resolves.toEqual([
      expect.objectContaining({ id: "lab-a" }),
    ]);
    await expect(createLabel("proj-1", { name: "Web", paths: [] })).resolves.toEqual(created);
    await expect(
      patchLabel("lab-b", { paths: [{ repo_id: "repo-1", path: "apps/web" }] }),
    ).resolves.toMatchObject({ id: "lab-b" });
    await expect(setTaskLabels("task-1", ["lab-a"])).resolves.toMatchObject({
      id: "task-1",
    });
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/projects/proj-1/labels?limit=100",
      expect.objectContaining({ credentials: "same-origin" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/v1/labels/lab-b",
      expect.objectContaining({ method: "PATCH" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      "/v1/tasks/task-1/labels",
      expect.objectContaining({ method: "PUT" }),
    );
  });
});
