import type { WorkerApi } from "./client.js";
import type { GithubImportJobData, GithubInvalidateJobData } from "./jobs.js";

export async function runGithubImportJob(
  data: GithubImportJobData,
  options: { api: WorkerApi },
): Promise<{ imported: number }> {
  const repo = await options.api.getRepo(data.repo_id);
  if (repo.project_id !== data.project_id) {
    throw new Error("repo project mismatch");
  }

  const issues =
    data.issue_number === undefined
      ? await options.api.listGithubIssues(repo.id, { state: "all" })
      : [await options.api.getGithubIssue(repo.id, data.issue_number)].filter(
          (item): item is NonNullable<typeof item> => Boolean(item),
        );

  if (issues.length === 0) {
    return { imported: 0 };
  }

  await options.api.upsertImportedIssues(
    repo.id,
    issues.map((issue) => ({
      github_issue_id: issue.id,
      number: issue.number,
      title: issue.title,
      body: issue.body,
    })),
    data.issue_number === undefined ? new Date().toISOString() : null,
  );
  return { imported: issues.length };
}

export async function runGithubInvalidateJob(
  data: GithubInvalidateJobData,
  options: { api: WorkerApi },
): Promise<{ recorded: boolean }> {
  const repo = await options.api.getRepo(data.repo_id);
  if (repo.project_id !== data.project_id) {
    throw new Error("repo project mismatch");
  }
  await options.api.recordGithubInvalidation(repo.id, {
    ref: data.ref,
    before: data.before,
    after: data.after,
  });
  return { recorded: true };
}
