# Beacon MCP host (stdio)

Local agents talk to Beacon file tools over stdio. HTTP MCP is not shipped.

```
dotnet run --project ProjectBeacon.Cli -- mcp --root <project-root>
```

Or after publish, `beacon mcp --root <project-root>`.

llama-swap is started by `beacon client` on the device, not by the Web host. The client pulls `GET /v1/devices/me/llamaswap-config` and reports proxy status in heartbeat `probeJson.llamaSwapStatus` plus `probeJson.hostLoad` (CPU/RAM/GPU). Backends with `Concurrent` emit a llama-swap `groups.resident` block (`swap: false`, `persistent: true`) so they can stay loaded together; unmarked backends stay in the default swap group. Launch commands for concurrent models must use `${PORT}`.

Local workstation client (outbound HTTPS to the control plane; no inbound port, no WSS file tunnel). In a terminal, `beacon client` with no token opens a Spectre.Console first-run walkthrough and a live dashboard. `--headless` keeps the old stderr loop.

```
dotnet run --project ProjectBeacon.Cli -- client
dotnet run --project ProjectBeacon.Cli -- client --url <api> --token <bcd_…> --headless
dotnet run --project ProjectBeacon.Cli -- client enroll --url <api> --login <user> --password <pass>
```

`BEACON_PROJECT_ROOT` overrides the working directory when `--root` is omitted.

## Tools

- `read_file` `{ path }`
- `write_file` `{ path, content }`
- `apply_patch` `{ path, oldText, newText }` — one exact occurrence; otherwise error, no partial write
- `get_tree` `{ path?, maxEntries? }` — re-scans the working tree; skips `node_modules`, `.git`, `bin`, `obj`, etc.
- `search_code` `{ query, path?, maxMatches? }` — literal substring search over text files, case-insensitive
- `get_changed_scope` `{ path?, maxFiles? }` — changed files from `git status`; the root must be a git repository

## Pipeline and model tools

These tools talk to the database and require Beacon environment:

- `model_bind` `{ role, modelBackendId }` — bind a pipeline role (`planner`, `actor`, `review`) to a model backend
- `model_status` `{}` — list model backends, role bindings and the llama-swap proxy status
- `task_create_subtask` `{ instructions, allowedMcpTools?, allowedPaths? }` — create a subtask on the session task
- `subtask_report_result` `{ subtaskId, diffRef, summary }` — report the result of an in-progress subtask
- `task_review_verdict` `{ verdict, note, subtaskId? }` — record a review verdict (`approve`, `reopen_subtask`)
- `task_pipeline_status` `{}` — show the pipeline state (subtasks, sessions, verdicts) of the session task

`allowedMcpTools` and `allowedPaths` are comma-separated lists.

When `BEACON_API_URL` and `BEACON_API_TOKEN` are set, these six tools call `/v1` instead of opening the database: `model_bind`, `model_status`, `task_create_subtask`, `subtask_report_result`, `task_review_verdict`, `task_pipeline_status`. Without that pair they keep the database path below.

## Control-plane tools

These tools call the same `/v1` routes the Web host serves. They need `BEACON_API_URL` (for example `http://127.0.0.1:5083`) and `BEACON_API_TOKEN` (project token `bcn_…` or a user JWT). `BEACON_PROJECT_ID` is the default project. `BEACON_TASK_ID` is the default task for `claim_task`, `context_compile`, `finish_work`, and the `pipeline_*` tools. A missing URL or token returns MCP `isError`; file tools keep working. There is no HTTP MCP server.

Board, backlog, and task detail:

- `list_tasks` `{ projectId?, status? }` — status `Todo`, `InProgress`, `Done`
- `get_task` `{ taskId?, projectId? }`
- `create_task` `{ title, description?, priority?, type?, labelId?, milestoneId?, path?, projectId? }`
- `update_task` `{ taskId, title?, description?, priority?, type?, labelId?, milestoneId?, projectId? }`
- `set_task_status` `{ taskId, status, projectId? }`
- `set_task_substage` `{ taskId, subStage }`
- `claim_task` `{ taskId?, projectId? }`
- `add_task_comment` `{ taskId, content, userId? }`
- `set_task_dependencies` `{ taskId, dependentTaskIds }`
- `add_review_notes` `{ taskId, reviewNotes }`
- `list_task_steps` `{ taskId }` / `add_task_step` `{ taskId, title }` / `toggle_task_step` `{ stepId, done }` / `delete_task_step` `{ stepId }`
- `finish_work` `{ taskId?, result, output?, actorId, review? }` — `result` is `done`, `failed`, `skipped`, or `partial`. `done` sends `review.reviewerRun`, `review.regressionsFound`, `review.regressionsFixed`

Context, decisions, roadmap, labels, reports:

- `context_compile` `{ taskId?, path?, repoId?, budgetTokens?, includeChangedScope?, includeTreeCapsule?, includeHandoff?, projectId? }`
- `list_context_nodes` / `get_context_node` `{ nodeId }` / `upsert_context_node` / `delete_context_node` `{ nodeId }` / `export_agents_md`
- `list_constraints` / `create_constraint` `{ body, kind }` / `activate_constraint` / `reject_constraint`
- `list_decisions` / `record_decision` `{ title, body, context?, consequences? }` / `accept_decision` / `deprecate_decision` / `supersede_decision` `{ decisionId, replacementId }`
- `list_milestones` / `get_milestone` / `create_milestone` / `update_milestone` / `delete_milestone` / `close_milestone` / `reopen_milestone`
- `list_labels` / `match_label` `{ path }` / `add_label_path` `{ labelId, path }`
- `list_reports` / `get_report` `{ reportId }` / `generate_report`

Pipeline actions that the task page has and the six database tools do not:

- `pipeline_start` `{ taskId? }`
- `pipeline_start_actor` `{ subtaskId, taskId? }`
- `pipeline_launch_session` `{ sessionId }`
- `pipeline_fail_subtask` `{ subtaskId, reason, taskId? }`
- `pipeline_start_review` `{ taskId? }`
- `pipeline_approve` `{ note?, taskId? }`
- `pipeline_force_close` `{ actorId, reason?, taskId? }` — API token needs Admin

Agents page writes that are not `model_bind` / `model_status`:

- `model_upsert` `{ name, backendType, launchCommand, contextSize, ttl, id?, extraFlags?, concurrent? }`
- `model_delete` `{ modelBackendId }`
- `model_unbind` `{ role }`
- `proxy_reload` / `proxy_unload`

Enum values on the wire match the API (`Todo`, `Planner`, `Must`, `Native`). Do not send `write_handoff`. Chat, settings, auth, and device commands stay off this tool list: chat needs a user session, and device commands stay on `beacon client`.

### Environment

| Variable | Required for | Meaning |
| --- | --- | --- |
| `BEACON_API_URL` | control-plane tools, and the API path of the six pipeline/model tools | Base URL of the Web or API host, such as `http://127.0.0.1:5083` |
| `BEACON_API_TOKEN` | same as `BEACON_API_URL` | Bearer token: project `bcn_…` or a user JWT. Not printed |
| `BEACON_PROJECT_ID` | pipeline, model, and project-scoped control-plane tools | GUID of the project this MCP session belongs to |
| `BEACON_TASK_ID` | the four `task_*` / `subtask_*` tools, and the default task for `claim_task`, `context_compile`, `finish_work`, `pipeline_*` | GUID of the session task |
| `BEACON_ACTOR_ID` | none (reserved) | actor identity; not enforced yet |

Without `BEACON_PROJECT_ID` (or without the connection configuration) the tools return an MCP `isError` result; the server keeps running, and the file tools stay available. The database provider is created lazily on the first database tool call. The llama-swap proxy is only available while a workstation client reports it in heartbeat (`probeJson.llamaSwapStatus`); otherwise `model_status` reports the proxy as unavailable.

For `search_code` and `get_changed_scope`, `path` is a comma-separated list of path prefixes; an empty or omitted value means the whole tree.

Paths are resolved inside the project root. `../` and other escapes are rejected. There is no shell tool.

## Workstation client commands

`beacon client` long-polls `GET /v1/devices/me/commands` (device token `bcd_`). Web never executes these on the server.

| Kind | Role |
|---|---|
| `probe` | Detect git, opencode, node, docker, llama-swap, llama-server |
| `list_dir` | One-level directory listing on the device |
| `init_project` | `.gitignore`, merge `opencode.json`, `.opencode/data` |
| `apply_opencode` | Merge or replace (`mcpReplace`) OpenCode config |
| `scan_gguf` | List `*.gguf` under `modelsRoot` |
| `install` | Allowlisted winget ids: git, node, docker |
| `save_workstation` | Persist `workstation.json` (models root, bins, history dir) |
| `reload_proxy` / `unload_proxy` | llama-swap config rewrite / HTTP unload |

Do not add an inbound listen port on the device. Do not use the flagged-off WSS sidecar tunnel.

## Deny native file tools on the host

OpenCode / comparable harness:

```json
{
  "permission": {
    "read": "deny",
    "edit": "deny",
    "glob": "deny",
    "grep": "deny"
  }
}
```

Grok (`~/.grok/config.toml`), stdio only:

```toml
[mcp_servers.beacon]
command = "dotnet"
args = ["run", "--project", "ProjectBeacon.Cli", "--", "mcp", "--root", "."]
enabled = true
```

Do not add an HTTP/SSE Beacon MCP server.
