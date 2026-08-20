import { afterEach, describe, expect, it, vi } from "vitest";

import {
  ApiError,
  createContextNode,
  fetchAllPages,
  putContextNode,
  readApiError,
  saveProjectBrief,
} from "./api";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("readApiError", () => {
  it("keeps API details so version conflicts can restore the server task", async () => {
    const error = await readApiError(
      new Response(
        JSON.stringify({
          error: {
            code: "version_conflict",
            message: "task changed",
            details: { task: { id: "task-1", version: 4 } },
          },
        }),
        { status: 409, headers: { "content-type": "application/json" } },
      ),
      "failed to update task",
    );

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(409);
    expect(error.code).toBe("version_conflict");
    expect(error.details).toEqual({ task: { id: "task-1", version: 4 } });
  });
});

describe("fetchAllPages", () => {
  it("follows next_cursor until the last page", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [{ id: "a" }], next_cursor: "page-2" }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ items: [{ id: "b" }], next_cursor: null }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    await expect(
      fetchAllPages<{ id: string }>("/v1/items", "failed to load items"),
    ).resolves.toEqual([{ id: "a" }, { id: "b" }]);
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/v1/items?limit=100",
      expect.objectContaining({ credentials: "same-origin" }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/v1/items?limit=100&cursor=page-2",
      expect.objectContaining({ credentials: "same-origin" }),
    );
  });
});

describe("context node client", () => {
  it("POSTs create without a client id so the API mints it", async () => {
    const created = {
      id: "01a01be5-3e7b-7c73-9e28-5d19f58f49b2",
      sections: [{ id: "goals", title: "Goals", body_md: "Ship.", ordinal: 0 }],
    };
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(created), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const body = { sections: created.sections, scope_type: "project", path: "" };
    await expect(createContextNode("proj-1", body)).resolves.toMatchObject({ id: created.id });
    expect(fetchMock).toHaveBeenCalledWith(
      "/v1/projects/proj-1/context/nodes",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(body),
      }),
    );
    expect(JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string)).not.toHaveProperty("id");
  });

  it("saves a wizard brief as a project-scope POST without a client id", async () => {
    const created = { id: "01a01be5-3e7b-7c73-9e28-5d19f58f49b2", sections: [] };
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(created), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const sections = [{ id: "goals" as const, title: "Goals", body_md: "Ship.", ordinal: 0 }];
    await expect(saveProjectBrief("proj-1", sections)).resolves.toMatchObject({ id: created.id });
    expect(JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string)).toEqual({
      sections,
      scope_type: "project",
      path: "",
    });
  });

  it("PUTs updates with the existing node id", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: "node-1" }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await putContextNode("proj-1", "node-1", { sections: [] });
    expect(fetchMock).toHaveBeenCalledWith(
      "/v1/projects/proj-1/context/nodes/node-1",
      expect.objectContaining({ method: "PUT" }),
    );
  });
});
