import { describe, expect, it } from "vitest";

import type { CodeGateway } from "../code/gateway.js";
import type { ProjectRepoRecord } from "./types.js";
import { compileProjectBrief } from "./compile-brief.js";

const PROJECT = { id: "018f1e2c-3d4e-7000-8000-000000000001", name: "Beacon", slug: "beacon" };
const REPO_ID = "018f1e2c-3d4e-7000-8000-000000000002";
const NOW = new Date("2026-08-17T12:00:00.000Z");

const REPO: ProjectRepoRecord = {
  id: REPO_ID,
  projectId: PROJECT.id,
  provider: "local",
  remoteUrl: null,
  defaultBranch: "main",
  githubRepoId: null,
  installationId: null,
  localRootHint: "/tmp/beacon",
  indexMode: "sidecar",
  lastIndexedSha: null,
  lastIndexedAt: null,
};

function storeWithRepo() {
  return {
    async findTaskById() {
      return undefined;
    },
    async findMilestoneById() {
      return undefined;
    },
    async listContextNodes() {
      return [];
    },
    async listActiveConstraints() {
      return [];
    },
    async listAcceptedDecisions() {
      return [];
    },
    async findLatestHandoffByTaskId() {
      return undefined;
    },
    async findProjectRepoById(id: string) {
      return id === REPO.id ? REPO : undefined;
    },
    async listProjectRepos() {
      return [REPO];
    },
    async findProjectById(id: string) {
      return id === PROJECT.id
        ? {
            id: PROJECT.id,
            orgId: "018f1e2c-3d4e-7000-8000-000000000099",
            slug: PROJECT.slug,
            name: PROJECT.name,
            description: "",
            visibility: "private" as const,
            defaultRepoId: REPO.id,
            settings: {},
            deletedAt: null,
            createdAt: NOW,
            updatedAt: NOW,
          }
        : undefined;
    },
    async listTaskLabels() {
      return [];
    },
  };
}

describe("compileProjectBrief", () => {
  it("does not call CodeGateway unless include asks for capsules", async () => {
    const queries: unknown[] = [];
    const gateway: CodeGateway = {
      async query(_repo, query) {
        queries.push(query);
        return { items: [] };
      },
      async health() {
        return true;
      },
    };
    const compiled = await compileProjectBrief(
      storeWithRepo(),
      PROJECT,
      { project_id: PROJECT.id },
      NOW,
      gateway,
    );
    expect(compiled.ok).toBe(true);
    expect(queries).toEqual([]);
  });

  it("queries the tree when include.tree_capsule is true", async () => {
    const queries: unknown[] = [];
    const gateway: CodeGateway = {
      async query(_repo, query) {
        queries.push(query);
        return { items: [{ path: "apps", children: ["api"] }] };
      },
      async health() {
        return true;
      },
    };
    const compiled = await compileProjectBrief(
      storeWithRepo(),
      PROJECT,
      { project_id: PROJECT.id, include: { tree_capsule: true } },
      NOW,
      gateway,
    );
    expect(compiled.ok).toBe(true);
    expect(queries).toEqual([{ kind: "tree", path: ".", depth: 2 }]);
    if (compiled.ok) {
      expect(compiled.compiled.brief.tree_capsule).toMatchObject({
        repo_id: REPO_ID,
        root: ".",
      });
    }
  });

  it("does not query CodeGateway when extras already supply capsules", async () => {
    const queries: unknown[] = [];
    const gateway: CodeGateway = {
      async query(_repo, query) {
        queries.push(query);
        return { items: [] };
      },
      async health() {
        return true;
      },
    };
    const extras = {
      tree_capsule: {
        repo_id: REPO_ID,
        root: ".",
        entries: [{ path: "apps", kind: "dir" as const }],
      },
    };
    const compiled = await compileProjectBrief(
      storeWithRepo(),
      PROJECT,
      { project_id: PROJECT.id, include: { tree_capsule: true }, extras },
      NOW,
      gateway,
    );
    expect(compiled.ok).toBe(true);
    expect(queries).toEqual([]);
    if (compiled.ok) {
      expect(compiled.compiled.brief.tree_capsule).toEqual(extras.tree_capsule);
    }
  });

  it("omits capsules when the gateway is missing", async () => {
    const compiled = await compileProjectBrief(
      storeWithRepo(),
      PROJECT,
      { project_id: PROJECT.id, include: { tree_capsule: true } },
      NOW,
    );
    expect(compiled.ok).toBe(true);
    if (compiled.ok) {
      expect(compiled.compiled.brief.tree_capsule).toBeNull();
      expect(compiled.compiled.brief.budget.dropped).toEqual(
        expect.arrayContaining(["tree_capsule"]),
      );
    }
  });
});
