# Beacon MCP host (stdio)

Local agents talk to Beacon file tools over stdio. HTTP MCP is not shipped.

```
dotnet run --project ProjectBeacon.Cli -- mcp --root <project-root>
```

Or after publish, `beacon mcp --root <project-root>`.

llama-swap is started by `beacon client` on the device, not by the Web host. The client pulls `GET /v1/devices/me/llamaswap-config` and reports proxy status in heartbeat `probeJson.llamaSwapStatus`.

Local workstation client (outbound HTTPS to the control plane; no inbound port, no WSS file tunnel):

```
dotnet run --project ProjectBeacon.Cli -- client --url <api> --token <bcd_…>
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

### Environment

| Variable | Required for | Meaning |
| --- | --- | --- |
| `BEACON_PROJECT_ID` | all pipeline and model tools | GUID of the project this MCP session belongs to |
| `BEACON_TASK_ID` | the four `task_*` / `subtask_*` tools | GUID of the session task |
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
