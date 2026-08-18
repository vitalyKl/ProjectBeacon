import type { WorkerApi } from "./client.js";
import type { WorkerIndexRegistry } from "./index-server.js";

export async function purgeProjectClones(
  api: WorkerApi,
  registry: WorkerIndexRegistry,
  projectId: string,
): Promise<string[]> {
  const purge = await api.projectClonePurge(projectId);
  if (!purge.deleted) {
    return [];
  }
  for (const repoId of purge.repo_ids) {
    await registry.dropRepo(repoId);
  }
  return purge.repo_ids;
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
