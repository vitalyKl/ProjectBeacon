# Phase 0 — Inventory of Routes and Their Purpose

Frozen baseline. Date: 2026-09-30.

## UI Routes (Blazor Server, `ProjectBeacon.Web`)

| Route | Component | Layout | Auth | Purpose |
|---|---|---|---|---|
| `/` | `Landing.razor` | `LandingLayout` | Anonymous | Product landing page (anonymous entry point) |
| `/login` | `Login.razor` | `AuthLayout` | Anonymous | User login (password + email) |
| `/register` | `Register.razor` | `AuthLayout` | Anonymous | Registration (invite token required if `AUTH_LOCAL_INVITE_ONLY`) |
| `/forgot` | `Forgot.razor` | `AuthLayout` | Anonymous | Forgot password (always returns 200, no email enumeration) |
| `/reset` | `Reset.razor` | `AuthLayout` | Anonymous | Password reset via token (`bcr_…`) |
| `/recover` | `Recover.razor` | `AuthLayout` | Anonymous | Bootstrap-token admin break-glass recovery |
| `/invite` | `Invite.razor` | `AuthLayout` | Anonymous | Invitation acceptance (`bci_…` token) |
| `/bootstrap` | `Bootstrap.razor` | `AuthLayout` | Anonymous | First-run bootstrap admin creation |
| `/dashboard` | `Dashboard.razor` | `MainLayout` | Authenticated | Main dashboard: project overview, stats, quick links |
| `/board` | `Board.razor` | `MainLayout` | Authenticated | Kanban board (Todo / InProgress / Done columns, drag-and-drop) |
| `/backlog` | `Backlog.razor` | `MainLayout` | Authenticated | Backlog task list (unprioritized tasks) |
| `/task/{taskId:guid}` | `TaskDetail.razor` | `MainLayout` | Authenticated | Task detail: description, steps, pipeline, comments, activity |
| `/context` | `Context.razor` | `MainLayout` | Authenticated | Context section: project brief nodes, compile, import/export |
| `/decisions` | `Decisions.razor` | `MainLayout` | Authenticated | Decision register: list, accept, deprecate, supersede |
| `/roadmap` | `Roadmap.razor` | `MainLayout` | Authenticated | Milestone roadmap: create, update, close, reopen milestones |
| `/reports` | `Reports.razor` | `MainLayout` | Authenticated | Reports: generate, list, view board snapshot reports |
| `/chat` | `Chat.razor` | `MainLayout` | Authenticated | Agent chat: sessions and prompts |
| `/settings` | `Settings.razor` | `MainLayout` | Authenticated | Account settings |
| `/settings/agents` | `Agents.razor` | `MainLayout` | Authenticated | User-level agents, models, task kinds |
| `/settings/workstations` | `Workstations.razor` | `MainLayout` | Authenticated | User-owned workstation runtime |
| `/settings/connections` | `OpenCodeConnections.razor` | `MainLayout` | Authenticated | User-level provider connections |
| `/project/settings` | `ProjectManage.razor` | `MainLayout` | Authenticated | Project members, API tokens, runtime binding |
| `/agents` | `AgentsRedirect.razor` | `MainLayout` | Authenticated | Redirect to `/settings/agents` |
| `/projects/new` | `New.razor` | `MainLayout` | Authenticated | Create new project |

**Embedded components (no `@page` directive, rendered within other pages):**

| Component | Rendered in | Purpose |
|---|---|---|
| `ProjectManage.razor` | Dashboard | Project members, API tokens, runtimes management |
| `Agents.razor` | Settings | Agent models, task kinds, MCP defaults, workstation binding |
| `TaskDialog.razor` | Board / Backlog | Create/edit task dialog |
| `TaskCard.razor` | Board | Kanban card representation |
| `PipelineDialog.razor` | TaskDetail | Pipeline subtask management dialog |
| `ChatDock.razor` | MainLayout (floating) | Quick chat dock (FAB + panel) |
| `ContextCostReport.razor` | Reports | Context token cost breakdown |
| `OpenCodeConnections.razor` | Settings/Agents | OpenCode connection management |
| `BackendDialog.razor` | Settings/Agents | Model backend CRUD dialog |
| `HostLoadView.razor` | Settings/Agents | Host resource load view |
| `TaskKindEditor.razor` | Settings/Agents | Task kind/template editor |

## API Routes (`/v1`, `ProjectBeacon.API`)

### Version

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/version` | Anonymous | Returns `{ version, gitSha }` |

### Auth

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/auth/login` | Anonymous | Login (email + password) → JWT session |
| POST | `/v1/auth/register` | Anonymous | Register (invite-gated if configured) |
| POST | `/v1/auth/logout` | User | Destroy the caller's session. User id comes from the principal. |
| POST | `/v1/auth/forgot-password` | Anonymous | Request password reset (always 200) |
| POST | `/v1/auth/reset-password` | Anonymous | Reset password via `bcr_` token |
| POST | `/v1/auth/invite/accept` | Anonymous | Accept invitation via `bci_` token |
| POST | `/v1/auth/bootstrap` | Bootstrap token | Create the first admin. Empty or wrong `BOOTSTRAP_ADMIN_TOKEN` is rejected. |
| POST | `/v1/auth/recover` | Anonymous | Bootstrap-token admin break-glass |

### Org

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/orgs` | User | Create organization |
| GET | `/v1/orgs` | User | List user's organizations |
| GET | `/v1/orgs/{id:guid}` | User | Get organization |
| PUT | `/v1/orgs/{id:guid}` | User | Update organization |
| DELETE | `/v1/orgs/{id:guid}` | User | Delete organization |
| POST | `/v1/orgs/{id:guid}/invites` | User | Create invite |
| GET | `/v1/orgs/{id:guid}/invites` | User | List invites |

### Project

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects` | User | Create project |
| GET | `/v1/projects` | User | List user's projects |
| GET | `/v1/projects/{id:guid}` | User | Get project |
| PUT | `/v1/projects/{id:guid}` | User | Update project |
| DELETE | `/v1/projects/{id:guid}` | User | Delete project |
| GET | `/v1/projects/{id:guid}/current` | User | Get current project (from session) |
| PUT | `/v1/projects/{id:guid}/current` | User | Set current project |
| POST | `/v1/projects/{id:guid}/tokens` | User | Create API token (`bcn_…`) |
| DELETE | `/v1/projects/{id:guid}/tokens/{tokenId:guid}` | User | Revoke API token |
| POST | `/v1/projects/{id:guid}/members` | User | Add project member |
| DELETE | `/v1/projects/{id:guid}/members/{userId:guid}` | User | Remove project member |

### Task

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects/{projectId:guid}/tasks` | User/Token | Create task |
| GET | `/v1/projects/{projectId:guid}/tasks` | User/Token | List tasks (filter by status, label, milestone) |
| GET | `/v1/tasks/{id:guid}` | User/Token | Get task (with comments, dependencies) |
| PATCH | `/v1/tasks/{id:guid}` | User/Token | Update task (title, description, status, priority, etc.) |
| DELETE | `/v1/tasks/{id:guid}` | User/Token | Delete task |
| POST | `/v1/tasks/{id:guid}/comments` | User/Token | Add comment |
| POST | `/v1/tasks/{id:guid}/steps` | User/Token | Add checklist step |
| DELETE | `/v1/tasks/steps/{stepId:guid}` | User/Token | Delete step |
| PATCH | `/v1/tasks/steps/{stepId:guid}` | User/Token | Toggle step done |
| PUT | `/v1/tasks/{id:guid}/dependencies` | User/Token | Set task dependencies |
| GET | `/v1/tasks/{id:guid}/steps` | User/Token | List steps |

### Milestone

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects/{projectId:guid}/milestones` | User/Token | Create milestone |
| PUT | `/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}` | User/Token | Update milestone |
| PUT | `/v1/milestones/{milestoneId:guid}` | User/Token | Update milestone (short form) |
| DELETE | `/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}` | User/Token | Delete milestone |
| DELETE | `/v1/milestones/{milestoneId:guid}` | User/Token | Delete milestone (short form) |
| GET | `/v1/projects/{projectId:guid}/milestones` | User/Token | List milestones |
| GET | `/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}` | User/Token | Get milestone |
| GET | `/v1/milestones/{milestoneId:guid}` | User/Token | Get milestone (short form) |
| POST | `/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}/close` | User/Token | Close milestone |
| POST | `/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}/reopen` | User/Token | Reopen milestone |

### Work

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/work/finish_work` | User/Token | Finish work on a task (result: done/failed/skipped/partial) |

### Pipeline

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/tasks/{id:guid}/pipeline/start` | User/Token | Start pipeline |
| POST | `/v1/tasks/{id:guid}/pipeline/subtasks` | User/Token | Create subtask |
| POST | `/v1/tasks/{id:guid}/pipeline/subtasks/{subtaskId:guid}/actor` | User/Token | Start actor session |
| POST | `/v1/tasks/{id:guid}/pipeline/subtasks/{subtaskId:guid}/fail` | User/Token | Fail subtask |
| POST | `/v1/tasks/{id:guid}/pipeline/subtasks/{subtaskId:guid}/result` | User/Token | Report subtask result |
| POST | `/v1/tasks/{id:guid}/pipeline/review` | User/Token | Start review stage |
| POST | `/v1/tasks/{id:guid}/pipeline/review/verdict` | User/Token | Record review verdict |
| POST | `/v1/tasks/{id:guid}/pipeline/approve` | User/Token | Approve pipeline (close task) |
| POST | `/v1/tasks/{id:guid}/pipeline/force-close` | User/Token | Force-close pipeline (admin) |
| POST | `/v1/tasks/{id:guid}/pipeline/session` | User/Token | Launch pipeline session |
| GET | `/v1/tasks/{id:guid}/pipeline` | User/Token | Pipeline status |

### Context

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/projects/{projectId:guid}/context` | User/Token | List context nodes |
| POST | `/v1/projects/{projectId:guid}/context` | User/Token | Create/update context node |
| DELETE | `/v1/projects/{projectId:guid}/context/{nodeId:guid}` | User/Token | Delete context node |
| GET | `/v1/projects/{projectId:guid}/context/{nodeId:guid}` | User/Token | Get context node |
| POST | `/v1/projects/{projectId:guid}/context/compile` | User/Token | Compile project brief |
| POST | `/v1/projects/{projectId:guid}/context/import` | User/Token | Import AGENTS.md |
| GET | `/v1/projects/{projectId:guid}/context/export` | User/Token | Export AGENTS.md |
| GET | `/v1/projects/{projectId:guid}/constraints` | User/Token | List constraints |
| POST | `/v1/projects/{projectId:guid}/constraints` | User/Token | Create constraint |
| POST | `/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/activate` | User/Token | Activate constraint |
| POST | `/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/reject` | User/Token | Reject constraint |

### Label

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/projects/{projectId:guid}/labels` | User/Token | List labels |
| GET | `/v1/projects/{projectId:guid}/labels/match?path=…` | User/Token | Match path to label |
| POST | `/v1/projects/{projectId:guid}/labels/{labelId:guid}/paths` | User/Token | Add path prefix to label |

### Report

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects/{projectId:guid}/reports` | User/Token | Generate report (client-supplied `CreatedByType`/`CreatedById` — P0 §1.5) |
| GET | `/v1/projects/{projectId:guid}/reports` | User/Token | List reports |
| GET | `/v1/projects/{projectId:guid}/reports/{reportId:guid}` | User/Token | Get report |
| GET | `/v1/reports/context-cost/{taskId:guid}` | User/Token | Context token cost report |

### Decision

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects/{projectId:guid}/decisions` | User/Token | Create decision |
| GET | `/v1/projects/{projectId:guid}/decisions` | User/Token | List decisions |
| POST | `/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/accept` | User/Token | Accept decision |
| POST | `/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/deprecate` | User/Token | Deprecate decision |
| POST | `/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede` | User/Token | Supersede decision |

### Model

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/projects/{projectId:guid}/models` | User/Token | List model backends |
| POST | `/v1/projects/{projectId:guid}/models` | User/Token | Upsert model backend |
| DELETE | `/v1/projects/{projectId:guid}/models/{modelBackendId}` | User/Token | Delete model backend |
| POST | `/v1/projects/{projectId:guid}/models/bind` | User/Token | Bind pipeline role to model |
| POST | `/v1/projects/{projectId:guid}/models/unbind` | User/Token | Unbind pipeline role |
| GET | `/v1/projects/{projectId:guid}/models/status` | User/Token | Model status + proxy status |
| POST | `/v1/projects/{projectId:guid}/proxy/reload` | User/Token | Reload llama-swap proxy |
| POST | `/v1/projects/{projectId:guid}/proxy/unload` | User/Token | Unload llama-swap proxy |

### Device

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/devices` | User | Create device (enroll workstation) |
| GET | `/v1/devices` | User | List user's devices |
| DELETE | `/v1/devices/{id:guid}` | User | Revoke device |
| POST | `/v1/devices/me/heartbeat` | DeviceToken | Heartbeat (probe + workstation status) |
| GET | `/v1/devices/me/commands?wait=…` | DeviceToken | Claim next command (long-poll) |
| POST | `/v1/commands/{id:guid}/complete` | DeviceToken | Complete command |
| POST | `/v1/devices/{id:guid}/commands` | User | Enqueue command to device |
| GET | `/v1/commands/{id:guid}` | User | Get command status |
| GET | `/v1/projects/{projectId:guid}/runtimes` | User | List project runtimes |
| POST | `/v1/projects/{projectId:guid}/runtimes` | User | Attach runtime (project ↔ device) |
| DELETE | `/v1/projects/{projectId:guid}/runtimes/{id:guid}` | User | Detach runtime |
| GET | `/v1/devices/me/llamaswap-config` | DeviceToken | Get llama-swap config |
| GET | `/v1/devices/me/opencode-connections` | DeviceToken | Get OpenCode connections |

### Chat

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/v1/projects/{projectId:guid}/chat/sessions` | User | Create chat session |
| GET | `/v1/projects/{projectId:guid}/chat/sessions` | User | List chat sessions |
| GET | `/v1/projects/{projectId:guid}/chat/sessions/{sessionId:guid}` | User | Get session (with messages) |
| DELETE | `/v1/projects/{projectId:guid}/chat/sessions/{sessionId:guid}` | User | Delete session |
| POST | `/v1/projects/{projectId:guid}/chat/sessions/{sessionId:guid}/prompt` | User | Send prompt (SSE streaming) |
| POST | `/v1/projects/{projectId:guid}/chat/sessions/{sessionId:guid}/abort` | User | Abort generation |

## Auth Model Summary

| Auth Type | Mechanism | Identity Claims | Scope |
|---|---|---|---|
| Anonymous | No auth required | None | Auth endpoints, `/v1/version`, `/` landing |
| User (JWT session) | `Authorization: Bearer <jwt>` | `NameIdentifier`, `email`, `username` | All `/v1/*` except device-token routes |
| API Token (`bcn_`) | `Authorization: Bearer bcn_…` | `ActorContext` (token id, bound project, capabilities). Secret is returned once on create; list/get return the prefix only. | That token's project only. Another project or a forged project header is 403. |
| Device Token (`bcd_`) | `Authorization: Bearer bcd_…` | `device_id` | Device-specific routes only |
| Bootstrap Token | `BOOTSTRAP_ADMIN_TOKEN` env | None (admin) | `/v1/auth/bootstrap`, `/v1/auth/recover` |

## Screen scope (sprint 2)

Actor is the signed-in user unless noted. Primary action is the job of the screen, not every button.

| Route | Scope | Resource | Primary action |
|---|---|---|---|
| `/`, `/login`, `/register`, `/forgot`, `/reset`, `/invite` | Anonymous | Account | Enter or recover an account |
| `/bootstrap`, `/recover` | Anonymous + bootstrap secret | Account | Create or reset the first admin |
| `/dashboard` | Project | Project overview | See current project status |
| `/board`, `/backlog`, `/task/{id}` | Project | Task | Move and inspect work |
| `/roadmap` | Project | Milestone | Plan milestones |
| `/context` | Project | Context | Compile the brief |
| `/decisions` | Project | Decision | Record and accept decisions |
| `/reports` | Project | Report | Generate a board snapshot |
| `/chat` | Project + workstation runtime | Chat session | Prompt the agent inside the project root |
| `/projects/new` | User | Project | Create a project and bind a folder |
| `/project/settings` | Project | Members, tokens, runtime | Administer the project |
| `/settings` | User | Account | Account and security |
| `/settings/agents` | User | Agent template, model | Configure agents. Apply still targets one workstation. |
| `/settings/workstations` | User-owned runtime | Device | See runtime health and queue lifecycle |
| `/settings/connections` | User | Provider connection | Save connection credentials |
| `/agents` | User | — | Shortcut to `/settings/agents` |

Screenshot pixels are not stored in git. The capture matrix (1440, 1280, 1024, 768, 390, 360) lives in `tests/playwright/screenshot-matrix.js`. Comparing those shots is the visual-regression sprint, not this baseline.
