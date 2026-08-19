import { describe, expect, it } from "vitest";

import { localRepoAttachInput, parseLocalRootHint } from "./local-root";

describe("parseLocalRootHint", () => {
  it("accepts a relative POSIX path and rejects traversal or absolute paths", () => {
    expect(parseLocalRootHint("apps/web")).toBe("apps/web");
    expect(parseLocalRootHint("./apps/web")).toBeUndefined();
    expect(parseLocalRootHint(".")).toBe(".");
    expect(parseLocalRootHint("./")).toBeUndefined();
    expect(parseLocalRootHint("/workspace/apps")).toBeUndefined();
    expect(parseLocalRootHint("../secret")).toBeUndefined();
    expect(parseLocalRootHint("C:/Windows")).toBeUndefined();
    expect(parseLocalRootHint("apps\\web")).toBe("apps/web");
  });
});

describe("localRepoAttachInput", () => {
  it("builds a local POST body for a valid hint", () => {
    expect(localRepoAttachInput("apps/api", "sidecar")).toEqual({
      ok: true,
      input: {
        provider: "local",
        index_mode: "sidecar",
        local_root_hint: "apps/api",
      },
    });
    expect(localRepoAttachInput(".", "bind_mount")).toEqual({
      ok: true,
      input: {
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: ".",
      },
    });
  });

  it("rejects an invalid hint without inventing a GitHub payload", () => {
    expect(localRepoAttachInput("../secret", "sidecar")).toEqual({ ok: false });
    expect(localRepoAttachInput("C:/Windows", "bind_mount")).toEqual({ ok: false });
  });
});
