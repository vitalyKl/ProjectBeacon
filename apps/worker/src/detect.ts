import path from "node:path";

import { parseLocalRootHint } from "./local-root.js";
import type { WorkerApi } from "./client.js";
import type { DetectJobData } from "./jobs.js";
import { scanDetectFiles } from "./scan.js";

export async function runDetectJob(
  data: DetectJobData,
  options: { api: WorkerApi; workspace?: string },
): Promise<{ imported: number; milestone_id: string | null; tasks: number }> {
  const repo = await options.api.getRepo(data.repo_id);
  if (repo.project_id !== data.project_id) {
    throw new Error("repo project mismatch");
  }

  const files =
    repo.provider === "local" && options.workspace
      ? await scanWorkspace(options.workspace, repo.local_root_hint)
      : [];

  if (files.length > 0) {
    await options.api.importContext(repo.project_id, repo.id, files);
  }

  const milestone = await options.api.createMilestone(repo.project_id, {
    title: "Detector skeleton",
    description: "Proposed first milestone from layout detect.",
  });

  const tasks = detectorTasks(
    repo.id,
    files.map((file) => file.path),
  );
  let created = 0;
  for (const [index, task] of tasks.entries()) {
    await options.api.createTask(
      repo.project_id,
      {
        title: task.title,
        description: task.description,
        milestone_id: milestone.id,
        type: "task",
        linked_paths: task.path ? [{ repo_id: repo.id, path: task.path }] : [],
      },
      `detect:${repo.id}:task:${index}`,
    );
    created += 1;
  }

  return { imported: files.length, milestone_id: milestone.id, tasks: created };
}

async function scanWorkspace(
  workspace: string,
  hint: string | null,
): Promise<{ path: string; content: string }[]> {
  const relative = parseLocalRootHint(hint ?? "");
  if (!relative) {
    return [];
  }
  const root = path.resolve(workspace, ...relative.split("/"));
  const workspaceRoot = path.resolve(workspace);
  if (root !== workspaceRoot && !root.startsWith(workspaceRoot + path.sep)) {
    return [];
  }
  return scanDetectFiles(root);
}

function detectorTasks(
  _repoId: string,
  paths: string[],
): Array<{ title: string; description: string; path: string | null }> {
  const tasks: Array<{ title: string; description: string; path: string | null }> = [
    {
      title: "Review imported project context",
      description:
        "Imported rules and stack hints need a human pass before agents treat them as reviewed.",
      path: null,
    },
  ];
  if (paths.some((item) => item.toLowerCase().endsWith("package.json"))) {
    tasks.push({
      title: "Confirm Node workspace layout",
      description: "Detector found package.json; confirm workspaces and default commands.",
      path: paths.find((item) => item.toLowerCase().endsWith("package.json")) ?? "package.json",
    });
  }
  if (
    paths.some(
      (item) => item.toLowerCase().includes("go.mod") || item.toLowerCase().endsWith("go.work"),
    )
  ) {
    tasks.push({
      title: "Confirm Go module layout",
      description: "Detector found Go manifests; confirm modules and commands.",
      path:
        paths.find(
          (item) => item.toLowerCase().endsWith("go.mod") || item.toLowerCase().endsWith("go.work"),
        ) ?? null,
    });
  }
  if (paths.some((item) => item.toLowerCase().endsWith("cargo.toml"))) {
    tasks.push({
      title: "Confirm Rust crate layout",
      description: "Detector found Cargo.toml; confirm crate roots and commands.",
      path: paths.find((item) => item.toLowerCase().endsWith("cargo.toml")) ?? "Cargo.toml",
    });
  }
  if (
    paths.some(
      (item) =>
        item.toLowerCase().endsWith("agents.md") || item.toLowerCase().endsWith("claude.md"),
    )
  ) {
    tasks.push({
      title: "Merge imported agent rules",
      description: "Existing AGENTS.md / CLAUDE.md were imported as needs_review.",
      path: paths.find((item) => /agents\.md$|claude\.md$/i.test(item)) ?? null,
    });
  }
  return tasks;
}
