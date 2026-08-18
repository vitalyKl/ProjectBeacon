import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, fetchAllPages, readApiError } from "./api";

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
