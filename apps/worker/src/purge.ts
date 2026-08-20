import type { WorkerApi } from "./client.js";
import type { WorkerIndexRegistry } from "./index-server.js";

export async function purgeProjectClones(
  api: WorkerApi,
  registry: WorkerIndexRegistry,
  projectId: string,
): Promise<string[]> {
  const repos = await api.listProjectRepos(projectId);
  const repoIds = repos.map((repo) => repo.id);
  for (const repoId of repoIds) {
    await registry.dropRepo(repoId);
  }
  return repoIds;
}

export async function purgeDeletedProjectClones(
  api: WorkerApi,
  registry: WorkerIndexRegistry,
): Promise<string[]> {
  const dropped: string[] = [];
  for (const project of await api.listDeletedProjects()) {
    dropped.push(...(await purgeProjectClones(api, registry, project.id)));
  }
  return dropped;
}
