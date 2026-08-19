import { createHttpCodeSource } from "./code-source.js";
import { invalidArguments, repoAmbiguous } from "./errors.js";
import { apiRequest, contextForProject, requireProjectId } from "./http.js";
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

function requestContext(
  args: { project_id?: string | undefined },
  ctx: InvokeContext,
): InvokeContext {
  const projectId = args.project_id;
  if (!projectId || projectId === ctx.projectId) {
    return ctx;
  }
  return contextForProject(ctx, projectId);
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

function scoped(ctx: InvokeContext, projectId: string): InvokeContext {
  if (!projectId || projectId === ctx.projectId) {
    return ctx;
  }
  return contextForProject(ctx, projectId);
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
    case "get_project": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}`,
      });
    }
    case "get_context_pack": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      const projectId = requireProjectId(data, scopedCtx);
      return apiRequest(scopedCtx, {
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
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/context/search`,
        query: {
          q: data.q,
          limit: data.limit,
        },
      });
    }
    case "get_task_brief": {
      const data = argsOf(tool, args);
      const projectId = await projectIdOfTask(data.task_id, ctx);
      const scopedCtx = scoped(ctx, projectId);
      return apiRequest(scopedCtx, {
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
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/milestones`,
        query: {
          include_closed: data.include_closed,
        },
      });
    }
    case "list_tasks": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/tasks`,
        query: {
          milestone_id: data.milestone_id,
          status: statusQuery(data.status),
          q: data.q,
          assignee: data.assignee,
          label_id: data.label_id,
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
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/tasks`,
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
    case "list_comments": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/tasks/${data.task_id}/comments`,
        query: {
          cursor: data.cursor,
          limit: data.limit,
        },
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
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/decisions`,
        query: {
          status: data.status,
          q: data.q,
          path_prefix: data.path_prefix,
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "list_labels": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/labels`,
        query: {
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "propose_label": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/labels`,
        body: omitKeys(data as JsonObject, ["project_id"]),
      });
    }
    case "set_task_labels": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "PUT",
        path: `/v1/tasks/${data.task_id}/labels`,
        body: { label_ids: data.label_ids },
      });
    }
    case "record_decision": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/decisions`,
        idempotencyKey: data.idempotency_key,
        body: omitKeys(data as JsonObject, ["project_id", "idempotency_key"]),
      });
    }
    case "get_constraints": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/constraints`,
        query: {
          path: data.path,
          active_only: data.active_only,
        },
      });
    }
    case "create_constraint": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/constraints`,
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
      const scopedCtx = scoped(ctx, projectId);
      return apiRequest(scopedCtx, {
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
          how_to_check: data.how_to_check,
          files_touched: data.files_touched,
          open_questions: data.open_questions,
          status: data.status,
        }),
      });
    }
    case "get_handoff": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/tasks/${data.task_id}/handoff`,
      });
    }
    case "list_reports": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/reports`,
        query: {
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "generate_report": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/reports`,
        body: omitUndefined({ title: data.title }),
      });
    }
    case "get_report": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/reports/${data.report_id}`,
      });
    }
    case "list_reviews": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "GET",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/reviews`,
        query: {
          cursor: data.cursor,
          limit: data.limit,
        },
      });
    }
    case "import_review": {
      const data = argsOf(tool, args);
      const scopedCtx = requestContext(data, ctx);
      return apiRequest(scopedCtx, {
        method: "POST",
        path: `/v1/projects/${requireProjectId(data, scopedCtx)}/reviews`,
        body: omitUndefined({
          title: data.title,
          body_md: data.body_md,
          source_path: data.source_path,
        }),
      });
    }
    case "get_review": {
      const data = argsOf(tool, args);
      return apiRequest(ctx, {
        method: "GET",
        path: `/v1/reviews/${data.review_id}`,
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
