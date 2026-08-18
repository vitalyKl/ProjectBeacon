import { createHttpCodeSource } from "./code-source.js";
import { handoffWriteUnavailable, invalidArguments, repoAmbiguous } from "./errors.js";
import { apiRequest, requireProjectId } from "./http.js";
import type { InvokeContext, JsonObject } from "./types.js";
import { isToolName, parseToolArgs, type ToolArgs, type ToolName } from "./tools.js";

function omitUndefined(value: JsonObject): JsonObject {
  const out: JsonObject = {};
  for (const [key, item] of Object.entries(value)) {
    if (item !== undefined) {
      out[key] = item;
    }
  }
  return out;
}

function omitKeys(value: JsonObject, keys: readonly string[]): JsonObject {
  const skip = new Set(keys);
  const out: JsonObject = {};
  for (const [key, item] of Object.entries(value)) {
    if (!skip.has(key) && item !== undefined) {
      out[key] = item;
    }
  }
  return out;
}

function statusQuery(status: string[] | undefined): string | undefined {
  if (!status || status.length === 0) {
    return undefined;
  }
  return status.join(",");
}

function codeSource(ctx: InvokeContext) {
  return ctx.codeSource ?? createHttpCodeSource();
}

function argsOf<T extends ToolName>(tool: T, args: unknown): ToolArgs<T> {
  const parsed = parseToolArgs(tool, args);
  if (!parsed.ok) {
    throw invalidArguments();
  }
  return parsed.data;
}

function resolveRepoId(repoId: string | undefined, ctx: InvokeContext): string {
  const resolved = repoId ?? ctx.defaultRepoId;
  if (!resolved) {
    throw repoAmbiguous();
  }
  return resolved;
}

async function projectIdOfTask(taskId: string, ctx: InvokeContext): Promise<string> {
  const task = (await apiRequest(ctx, {
    method: "GET",
    path: `/v1/tasks/${taskId}`,
  })) as { project_id?: unknown };
  if (typeof task.project_id !== "string") {
    throw invalidArguments("task is missing project_id");
  }
  return task.project_id;
}

async function invokeKnown(tool: ToolName, args: unknown, ctx: InvokeContext): Promise<unknown> {
  switch (tool) {
    case "github_list_prs": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${resolveRepoId(data.repo_id, ctx)}/github/pulls`,
        query: {
          state: data.state,
          task_id: data.task_id,
        },
      });
    }
    case "github_list_issues": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/repos/${resolveRepoId(data.repo_id, ctx)}/github/issues`,
        query: {
          state: data.state,
          q: data.q,
        },
      });
    }
    case "github_link_issue": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/tasks/${data.task_id}/github-issue`,
        body: omitUndefined({
          issue_number: data.issue_number,
          repo_id: data.repo_id ?? ctx.defaultRepoId,
        }),
      });
    }
    case "github_sync_now": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/repos/${resolveRepoId(data.repo_id, ctx)}/github/sync`,
      });
    }
    case "get_project":
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(argsOf(tool, args), ctx)}`,
      });
    case "get_context_pack": {
      const data = argsOf(tool, args);
      const projectId = requireProjectId(data, ctx);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${projectId}/context/compile`,
        body: omitUndefined({
          project_id: projectId,
          repo_id: data.repo_id,
          path: data.path,
          task_id: data.task_id,
          budget_tokens: data.budget_tokens,
        }),
      });
    }
    case "search_context": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, ctx)}/context/search`,
        query: {
          q: data.q,
          limit: data.limit,
        },
      });
    }
    case "get_task_brief": {
      const data = argsOf(tool, args);
      const projectId = await projectIdOfTask(data.task_id, ctx);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${projectId}/context/compile`,
        body: omitUndefined({
          project_id: projectId,
          task_id: data.task_id,
          path: data.path,
          budget_tokens: data.budget_tokens,
        }),
      });
    }
    case "list_milestones": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, ctx)}/milestones`,
        query: {
          include_closed: data.include_closed,
        },
      });
    }
    case "list_tasks": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, ctx)}/tasks`,
        query: {
          milestone_id: data.milestone_id,
          status: statusQuery(data.status),
          q: data.q,
          assignee: data.assignee,
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "get_task": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/tasks/${data.task_id}`,
      });
    }
    case "create_task": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, ctx)}/tasks`,
        idempotencyKey: data.idempotency_key,
        body: omitKeys(data as JsonObject, ["project_id", "idempotency_key"]),
      });
    }
    case "update_task": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "PATCH",
        path: `/v1/tasks/${data.task_id}`,
        body: omitKeys(data as JsonObject, ["task_id"]),
      });
    }
    case "add_comment": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/tasks/${data.task_id}/comments`,
        idempotencyKey: data.idempotency_key,
        body: { body: data.body },
      });
    }
    case "set_status": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/tasks/${data.task_id}/status`,
        body: {
          status: data.status,
          expected_version: data.expected_version,
        },
      });
    }
    case "link_dependency": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/tasks/${data.from_task_id}/dependencies`,
        body: {
          to_task_id: data.to_task_id,
          type: data.type,
        },
      });
    }
    case "list_decisions": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, ctx)}/decisions`,
        query: {
          status: data.status,
          q: data.q,
          path_prefix: data.path_prefix,
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "record_decision": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, ctx)}/decisions`,
        idempotencyKey: data.idempotency_key,
        body: omitKeys(data as JsonObject, ["project_id", "idempotency_key"]),
      });
    }
    case "get_constraints": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, ctx)}/constraints`,
        query: {
          path: data.path,
          active_only: data.active_only,
        },
      });
    }
    case "create_constraint": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, ctx)}/constraints`,
        idempotencyKey: data.idempotency_key,
        body: omitKeys(data as JsonObject, ["project_id", "idempotency_key"]),
      });
    }
    case "apply_constraint": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/constraints/${data.constraint_id}/apply`,
        body: {},
      });
    }
    case "get_tree":
      return codeSource(ctx).getTree(argsOf(tool, args), ctx);
    case "search_code":
      return codeSource(ctx).searchCode(argsOf(tool, args), ctx);
    case "get_file":
      return codeSource(ctx).getFile(argsOf(tool, args), ctx);
    case "get_symbol":
      return codeSource(ctx).getSymbol(argsOf(tool, args), ctx);
    case "get_owners":
      return codeSource(ctx).getOwners(argsOf(tool, args), ctx);
    case "get_related_files":
      return codeSource(ctx).getRelatedFiles(argsOf(tool, args), ctx);
    case "get_changed_scope":
      return codeSource(ctx).getChangedScope(argsOf(tool, args), ctx);
    case "start_work": {
      const data = argsOf(tool, args);
      const projectId = await projectIdOfTask(data.task_id, ctx);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/projects/${projectId}/sessions`,
        idempotencyKey: data.idempotency_key,
        body: omitUndefined({
          task_id: data.task_id,
          path: data.path,
          steal: data.steal,
          budget_tokens: data.budget_tokens,
        }),
      });
    }
    case "finish_work": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "POST",
        path: `/v1/sessions/${data.session_id}/finish`,
        body: omitUndefined({
          summary: data.summary,
          next_steps: data.next_steps,
          files_touched: data.files_touched,
          open_questions: data.open_questions,
          status: data.status,
        }),
      });
    }
    case "write_handoff":
      argsOf(tool, args);
      throw handoffWriteUnavailable();
    case "get_handoff": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/tasks/${data.task_id}/handoff`,
      });
    }
    default:
      throw invalidArguments(`unknown tool: ${tool}`);
  }
}

export async function invoke(tool: string, args: unknown, ctx: InvokeContext): Promise<unknown> {
  if (!isToolName(tool)) {
    throw invalidArguments(`unknown tool: ${tool}`);
  }
  return invokeKnown(tool, args, ctx);
}
