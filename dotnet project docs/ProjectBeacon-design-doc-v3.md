# ProjectBeacon — Design Document v3

Authoritative architecture. Execution order stays in `ProjectBeacon-master-roadmap-v1.md`. Backend requirements stay in `ProjectBeacon-code-review-roadmap-v3.md`. UI requirements stay in `ProjectBeacon-ui-ux-review-roadmap-v1.1.md` and `UI Design Migration Specification.md`.

There is no `ProjectBeacon-roadmap-v3.md`. That name in the backend roadmap meant this trio, not a fourth plan. `archive/docs/` is history. Do not implement from it.

If this file and the code disagree, the code wins and this file should be corrected.

## 1. Product

Beacon is a control plane for a solo developer or a small team running coding agents on hardware they control. It compiles a budgeted context brief, keeps tasks and decisions, and orchestrates local model and workstation state. It does not execute an agent turn inside the Web or API process.

## 2. Non-goals

- No Beacon-hosted coding agent.
- No HTTP MCP server. Agents use stdio `beacon mcp`.
- No hosted clone (`ff.hosted_clone`) and no outbound WSS sidecar tunnel.
- No `write_handoff`. Completion is `finish_work`.
- GitHub two-way sync stays off. Import-only is the v1 forge path.
- No new UI kit. Web uses MudBlazor. Tokens live in `ProjectBeacon.Web/Theme/DesignTokens.cs`.
- `AGENTS.md` is a repo export for hosts that only read the tree. The living brief is Context. Do not treat the checked-in file as a second source of product decisions.

## 3. Scope

```text
User
├── Account / security
├── Agents & models          user-owned registry, templates, MCP defaults, OpenCode connections
├── Workstations             user-owned devices (not tenant-filtered)
└── Projects
    ├── Project settings
    ├── ProjectRuntime       (ProjectId, DeviceId, LocalRoot) — no single project RootPath
    ├── Tasks / pipeline
    ├── Context / decisions / roadmap / reports / labels
    └── Chat                 project session, executed on the bound device
```

Agents are not a project feature. `/agents` redirects to `/settings/agents`. Applying a template still writes that project's role bindings.

## 4. Processes

| Piece | Project | Job |
|---|---|---|
| API | `ProjectBeacon.API` | Thin `/v1` endpoints. Also mapped by the Web host. |
| Web | `ProjectBeacon.Web` | Blazor Server + MudBlazor. Calls Application handlers in-process. Does not read the user's disk and does not start model processes. `:5083` http / `:7118` https. |
| Workstation client | `ProjectBeacon.Cli` (`beacon client`) | Outbound HTTPS: enroll, heartbeat, long-poll commands. Owns the working tree, OpenCode config, and the model process. |
| MCP | `beacon mcp` | Stdio file tools on `--root`, plus control-plane tools when an API URL and token are set. |
| Worker | `ProjectBeacon.Worker` | Expires sessions, API tokens, password-reset tokens, and invites. Unscoped database role. No workstation commands, no model process, no GitHub sync. |

Dependency graph: Domain ← Application ← Infrastructure. Hosts reference Application and Infrastructure. Application does not project-reference Infrastructure. Persistence ports (`IBeaconDb`) are declared in Application.

Business writes go through Application handlers. Razor must not inject `BeaconDbContext`.

## 5. Security boundaries

- Auth endpoints are rate-limited. Passwords are BCrypt in Infrastructure.
- Bootstrap is fail-closed. Empty `BOOTSTRAP_ADMIN_TOKEN` does not skip the check.
- Logout requires authentication. The user id comes from the principal, not the body.
- `ActorContext` is `(UserId, IsAdmin, IsApiToken)`, built from the principal.
- API tokens (`bcn_`) are project-scoped. Capabilities: `TaskRead`, `TaskWrite`, `SessionDrive`, `ContextRead`, `Admin`. A token cannot cross projects.
- Device tokens (`bcd_`), invites (`bci_`), and password-reset tokens (`bcr_`) are shown once. Only SHA-256 hashes are stored.
- `BEACON_WORKER_TOKEN` is an `ActorId` string for `finish_work` and pipeline force-close. It does not make the caller a project admin. The Worker process does not read it.
- Tenant filters are fail-closed. A null project/org filter returns no rows. `Guid.Empty` matches nothing. Unscoped access is `TenantScope.EnterUnscoped()` (bootstrap, migrations, tests, Worker cleanup). Postgres connections apply `TenantRlsSession` from that scope.
- Invite and password-reset lookup uses `IgnoreQueryFilters` because the actor is not in the tenant yet.
- Forgot-password always returns 200. `/recover` is bootstrap-token admin break-glass, not user reset.
- `AUTH_LOCAL_INVITE_ONLY=true` requires an invite on register.
- Member roles are `Owner`, `Admin`, `Member`. Project and token mutations go through `ProjectAuthorization`.

Workstation boundary: device commands (`ListDir`, `InitProject`, `ApplyOpencode`, `SaveWorkstation`, `Install`, `ReloadProxy`, `UnloadProxy`, …) run only on the selected online device. Kind values are the `WorkstationCommandKind` names. File MCP tools stay inside `--root`. There is no shell tool on MCP.

## 6. Model runtime

The control plane owns the user-level model registry and generates llama-swap `config.yaml`. It does not start the process.

`beacon client` (`ClientLlamaSwap`) starts it. `UseOwnSwapper` selects the in-client backend (`IModelBackend` / `LlamaServerBackend`, direct `llama-server`). Otherwise the client launches external `llama-swap`. Status is reported in heartbeat `probeJson.llamaSwapStatus` and read by `DeviceLlamaSwapProxy`. `LlamaSwapSupervisor` is for unit tests only.

Backend types: `FreeToken`, `LlamaCpp`, `OpenAiCompatible`.

`Concurrent` backends are emitted in `groups.resident` (`swap: false`, `persistent: true`). Other backends stay in the swap group. Concurrent launch commands must contain `${PORT}`.

Desired and applied revisions exist so the client can reconcile configuration. Connectivity alone is not a healthy model runtime.

## 7. Agent runtime and pipeline

`IAgentRuntime` (`CreateSession`, `SendPrompt`, `StreamParts`, `Abort`, `ReadUsage`) is implemented on the client by `OpenCodeAgentRuntime`. That is the workstation/chat harness path.

Pipeline role sessions still use `ManualSessionSpawner` (`ISessionSpawner` in Application). Do not describe the client as the pipeline harness.

Pipeline phases, each a new session:

1. Planner sees the task and may create subtasks.
2. Actor sees only that subtask's instructions. `AllowedMcpTools` and `AllowedPaths` are stored on the subtask and copied into the actor prompt. They are not harness permission denies.
3. Review sees the task text plus each subtask's instructions, diff ref, and summary. It does not see the other transcripts.
4. Verdict is approve or reopen-subtask. Force-close is a separate path.

Prompt text is `SessionPrompts`. This product review is not the `cold-diff-review` skill. That skill is an isolated diff reading before push. See `Skills/cold-diff-review.md`.

`finish_work` takes `done` / `failed` / `skipped` / `partial`. `done` requires `reviewRunId` for a completed check-proof review run, plus review notes or output. Optional `review` carries `reviewerRun`, `regressionsFound`, and `regressionsFixed`. The actor is the authenticated principal; the body has no `actorId`.

## 8. MCP trust

Two different paths. Do not treat them as the same authority.

| Path | When | Trust |
|---|---|---|
| Local database | `beacon mcp` without `BEACON_API_URL` and `BEACON_API_TOKEN` | Trusted local process. No API-token scope, no actor capabilities, no rate limit. Tenant session still set from `BEACON_PROJECT_ID`. `BEACON_ACTOR_ID` is not enforced. |
| Control plane | Both URL and token set | Authenticated `/v1`. Project token or user JWT. File tools still stay inside `--root`. |

Setting only one of URL or token is a misconfiguration: those tools error and do not open the database.

Tool list and environment: `mcp-host.md`.

## 9. Context

Compile merges sections by scope, applies a token budget (default 8000), and never drops non-goals, security, or definition of done. The brief is what an agent should receive instead of rediscovering the repo. `AGENTS.md` export is a projection of that brief, not the compiler.

## 10. UI shell

MudBlazor. Light and dark tokens live in `DesignTokens.cs` (panel radius 12px, small radius 8px, control radius 5px, hairline 1px). Dark is the default. The header toggles Dark/Light; Settings adds System. The choice is the `beacon-theme` cookie and drives both MudBlazor `IsDarkMode` and the CSS tokens. Status chips use `StatusChip` and `ChipPalette`. Chrome strings go through `IStringLocalizer<Web>`.

Drawer groups in `MainLayout`: Operate, Intelligence, Govern. Width is `236px` (`BeaconTheme.LayoutProperties.DrawerWidthLeft`). `/project/settings` lists the user's projects and renames or deletes the open one. Overview shows workstation CPU, memory, and GPU. Routes are listed in the UI specification. `ChatDock` is a shell FAB; `/chat` is the full page. The drawer collapses below `Breakpoint.Md`. Anonymous `/` is the landing page.

## 11. Deploy

Self-host: Postgres via Compose, Web via `dotnet run --project ProjectBeacon.Web --launch-profile http`. Kubernetes blue-green: `deploy/README.md`. Production pods set `BEACON_MIGRATE_ON_START=false`. Version is `Version` in `Directory.Build.props`.

## 12. Document map

| Question | Read |
|---|---|
| What to do next | Master roadmap, then the section it names |
| How the system is shaped | This file |
| Security / architecture requirements | Code-review roadmap v3 |
| UI behavior and visuals | UI/UX roadmap v1.1, then `UI Design Migration Specification.md` |
| MCP tools | `mcp-host.md` |
| Pipeline contract | `task-pipeline-local-agents.md` |
| Feature index | `features.md` |
| Diff-review technique | `Skills/cold-diff-review.md` |
| Agent session prompt | `agent-prompt-template.md` |

## 13. Gaps (recorded, not fixed here)

- Subtask tool and path scope is prompt text only.
- Pipeline spawn is still `ManualSessionSpawner`.
