# ProjectBeacon (.NET) — Roadmap & Status, v2

Supersedes `ProjectBeacon-roadmap-v1.md` and `roadmap-progress.md`. Companion
to `ProjectBeacon-design-doc-v2.md`; if they disagree, the design doc wins.

Audit 2026-09-09: overlay Phase 0.5–4 that duplicated Identity/Projects/Tasks/
Context numbering was removed. Marks below follow the legend. An agent may
flip a row to ✅ only after running that row's acceptance criterion.

Follow-up 2026-09-10: closed remaining Phase 0–6 implementation debt. Report
and LabelPath entities + EF migrations. Auto-label is a live API tool
(`GET …/labels/match`, create-task `path`). Generate/list reports. Live
stdio MCP process harness (`dotnet exec beacon.dll mcp`) covers `tools/list`
write + `../` reject. Cookie-login HTTP: 302 + `BeaconAuth`. Org/project/
member/token/task HTTP create-read-list. Culture cookie via `GET /culture`.
API-only host serves `/v1` (401 without auth). Wizard handlers create
org+project+starter labels. `PATCH /v1/tasks/{id}/status` goes through
`ChangeTaskStatusHandler`; missing review notes → 400. Env: `.env` load +
`POSTGRES_PASSWORD`. Wizard now adds the creating user as org/project Owner.
Task Detail meter verified in Edge (52/8000 after Compile brief). Reports
Generate produced a board snapshot. Board DnD Todo→InProgress verified
(snackbar + column count). Decisions create+accept verified. Tree/ChangedScope
still stubbed until Phase 7.

Follow-up 2026-09-14: admin password recovery shipped. `POST
/v1/auth/recover-admin` (bootstrap token via Bearer, rate-limited,
fail-closed) resets the admin password and clears lockout; `/recover` page
linked from `/login`. Bootstrap endpoint status codes corrected: 409 only
for "Bootstrap already completed.", 401 for invalid/missing token. API test
factories fixed to send the bootstrap Bearer token.

Follow-up 2026-09-18: user registration, org/project invites, email
password reset, product version, and a public landing at `/` (anonymous
200; logged-in users go to dashboard). `AUTH_LOCAL_INVITE_ONLY` gates
`POST /v1/auth/register`. Invites persist `TokenHash` only (`bci_` shown
once). Forgot-password is always 200; reset consumes a one-hour token
and deactivates sessions. SMTP via `MAIL__*` (`IEmailSender`); missing
mail logs the link in Development. Product version is `0.1.0` in
`Directory.Build.props`; `GET /v1/version` is `{ version, gitSha }`;
Docker/release stamp `BEACON_VERSION` separately from SHA. Handler +
HTTP tests green; Postgres migrate of `AddAuthInvitesAndPasswordReset`
not yet run on a live host — rows 10–13 stay 🟡.

## 0. Status legend

| Mark | Means |
|---|---|
| ✅ Done | Acceptance criterion was run and passed on the real target (Postgres if the criterion says Postgres). |
| 🟡 Implemented, unverified | Code matches the shape; criterion not run, or run on a substitute. |
| ⏳ Pending | Waiting on a dependency. |
| 🔴 Blocked / incorrect | Marked done earlier but the implementation does not satisfy the criterion. |
| ⬜ Not started | Not started. |

A parent is only ✅ if every child is ✅.

## 1. Cross-cutting rules

- Rate limiting = time-windowed attempt counter, not a concurrency limiter.
- AGENTS.md = repository conventions only. DoD lives in the constraint system.
- Concurrency-sensitive SQL is not Done on SQLite.
- A table row with sub-steps is only as done as its least-done sub-step.

## 2. Phase 0 — Hygiene and foundation

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | `Entity.New()` / `Create()` | ✅ | Domain.Tests factory calls cover every entity including DecisionTask, LabelPath, Report (62 passed 2026-09-10) |
| 2 | `.gitignore` | ✅ | `bin/`, `obj/`, `.env`, `TestResults/` ignored; `global.json` and `.env.example` tracked |
| 3 | .NET scaffold committed | ✅ | `git log` shows commits containing the .NET solution |
| 4 | EF ModelSnapshot | ✅ | `Migrations/BeaconDbContextModelSnapshot.cs` exists; `dotnet ef migrations has-pending-model-changes` reports none |
| 5 | DB migrate at startup | ✅ | Fresh Postgres + `dotnet run` creates tables |
| 6 | xUnit only | ✅ | No `[TestMethod]` remains; `dotnet test` is xUnit |
| 7 | `global.json` pins SDK | ✅ | File exists (`10.0.401` / `latestPatch`) and is tracked. CI reads `global-json-file: global.json`. |
| 8 | CI build+test | 🟡 | Workflow installs SDK from `global.json` and runs `dotnet test`; default-branch green not yet proven. |
| 9 | AGENTS.md conventions-only | 🟡 | No DoD section; no task-specific instructions |
| 10 | `TaskItemStatus` | ✅ | `dotnet build` has no CS0104 `TaskStatus` clash |
| 11 | resx + LanguageSwitcher | ✅ | Locales en/ru/de/ja/zh. `GET /culture` sets `.AspNetCore.Culture` (`CookieLoginHttpTests.Culture_SetsCookie_AndRejectsUnknown`). No custom JS; no `Thread.CurrentThread` mutation. |

CQRS split (handlers in Application, thin API endpoints) is in place. Web
screens under `Features/` call those handlers; Razor must not inject
`BeaconDbContext`. `/v1` is mapped on the Web host as well as the API
project.

## 3. Phase 1 — Identity and security

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | API project split | ✅ | `/v1/*` responds with only `ProjectBeacon.API` running (`ApiHost_V1RequiresAuth_AndAssemblyIsApi`) |
| 2 | `UserSession`; lockout columns | ✅ | Schema has `LastLoginAt`, `FailedLoginAttempts`, `LockedUntil` |
| 3 | JWT for API, cookie for Blazor | ✅ | Bearer `/v1/*` works without cookie; Blazor page load works without bearer. HTTP: JWT login 200, cookie-login 302 + `BeaconAuth` (`CookieLoginHttpTests`). Edge Playwright: cookie-login → Blazor circuit (wizard, New Task, Compile brief, Generate report) with no bearer. |
| 4 | Rate limiting bootstrap/login/register | ✅ | N+1th auth request from same IP in the window returns 429 (`AuthHttpTests.AuthEndpoints_RateLimited`) |
| 5 | Bootstrap random password, BCrypt in Infrastructure | ✅ | No `admin123`; bootstrap uses random 32-char password; BCrypt package only in Infrastructure |
| 6 | Login/Bootstrap/auth guard | ✅ | Anonymous `/board` redirects to login |
| 7 | Auth integration tests | ✅ | Handler tests on SQLite + HTTP factory tests (`AuthIntegrationTests`, `AuthHttpTests`) |
| 8 | Cross-tenant isolation proven | ✅ | Query filters on; `TenantIsolationPostgresTests` pass |
| 9 | Admin password recovery | ✅ | `POST /v1/auth/recover-admin` (bootstrap token, rate-limited, fail-closed) resets admin password + lockout; `/recover` page. HTTP: `RecoverAdmin_ValidToken_ResetsPassword`, `RecoverAdmin_InvalidToken_Unauthorized`, `RecoverAdmin_NoAdmin_BadRequest`, `RecoverAdmin_TokenNotConfigured_Unauthorized` (28/28 API tests passed 2026-09-14) |
| 10 | Register + invites | 🟡 | Open `POST /v1/auth/register` or invite-only via `AUTH_LOCAL_INVITE_ONLY`. Org/project invites (`bci_` once, hashed). `/register`, `/invite`, Settings members. |
| 11 | Email password reset | 🟡 | `POST /v1/auth/forgot-password` (always 200) + `POST /v1/auth/reset-password`; `/forgot` `/reset`. SMTP via `MAIL__*`. `/recover` remains break-glass. Change-password in Settings. |
| 12 | Product version | 🟡 | `Version` in `Directory.Build.props`; `GET /v1/version` `{version, gitSha}`; drawer footer; MCP/clientVersion from assembly. Docker stamps `BEACON_VERSION` not SHA. |
| 13 | Public landing | 🟡 | Anonymous `GET /` is 200 (not 301 to dashboard). Logged-in `/` goes to dashboard. `CookieLoginHttpTests.Landing_Anonymous_Ok_NotPermanentRedirect`. |

## 4. Phase 2 — Projects, members, API tokens

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Org/member/invite/token entities | ✅ | Migration applies on fresh DB |
| 2 | Domain in `Identity/` + `Projects/` | ✅ | No entity files in `Domain/Entities` root |
| 3 | API for orgs/projects/members/tokens | ✅ | Create/read/list HTTP tests (`OrgProjectTaskHttpTests`) including `GET …/tokens` |
| 4 | Capabilities 403 | ✅ | `task:read` token gets 403 on write (`TaskReadToken_CannotCreateTask`) |
| 5 | Create-project wizard | ✅ | `/projects/new` creates org+project through handlers (`CreateProjectWizardTests`) |
| 6 | Token hashing, shown once | ✅ | Raw `bcn_` not persisted; list/get return prefix only |
| 7 | Cross-tenant query filters | ✅ | `IOrgScoped`/`IProjectScoped` filters parameterized via `BeaconDbContext`. Postgres tests pass: other-project tasks hidden; `IgnoreQueryFilters` still sees them. Hosted multi-tenant flag stays off. |

## 5. Phase 3 — Tasks, Board, Task Detail

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| A | DB credentials from env | ✅ | `.env` loaded at startup (`EnvFileTests`); `ConnectionStrings:Default` and/or `POSTGRES_PASSWORD`. Development falls back to `beacon` if both missing. |
| 1 | Milestone/dependency/comment | ✅ | Entities + Domain.Tests + HTTP comments/dependencies |
| 2 | Atomic claim | ✅ | `FOR UPDATE SKIP LOCKED` in a transaction. Testcontainers Postgres 16: 3 concurrent claims → 1 success (Todo→InProgress), 2 fail "already claimed". Sequential second claim fails. |
| 3 | Task API | ✅ | CRUD + comments + dependencies (`Task_CrudCommentsAndDependencies`) |
| 4 | Board/Backlog/Roadmap/TaskDetail | ✅ | Edge Playwright: drag `.mud-drop-item` Todo→InProgress calls `ChangeTaskStatusHandler`; snackbar "Task moved to InProgress"; dashboard In Progress count 1. Missing review notes still fail in handler tests. |
| 5 | Cold-diff gate InProgress→Done | ✅ | Domain rejects without review notes; `finish_work` requires structured review for `done` |
| 6 | Nav | ✅ | Drawer: Dashboard, Board, Backlog, Roadmap, Context, Decisions, Agents, Reports, Settings |

`POST /v1/work/finish_work` exists, is authorized, takes structured review,
treats `BEACON_WORKER_TOKEN` as worker. HTTP flow test: `FinishWork_Done_RequiresStructuredReview`.

## 6. Phase 4 — Context compilation

Entity layer, tokenizer, CRUD, compile, import/export exist. Product
acceptance from design-doc §4:

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Constraint entity | ✅ | Kind/Status, Activate/Reject (`ConstraintHandlerTests`) |
| 2 | Decision.Consequences in brief | ✅ | Compile test asserts consequences text |
| 3 | SharpToken vs heuristic | ✅ | Unit test RU/EN samples; real tokenizer ≠ heuristic on RU |
| 4 | Contradiction uses Kind | ✅ | Must+MustNot same ScopePath, non-English body |
| 5 | DoD as project Must | ✅ | Brief includes it |
| 6 | Tree/ChangedScope default when repo linked | 🟡 | Stub sections until Phase 7 index |
| 7 | `## Tools for this task` | ✅ | Compile test asserts `` `claim_task` `` |
| 8 | Budget uses real tokenizer | ✅ | SharpToken `cl100k_base` |
| 9 | Never-drop under tiny budget | ✅ | Task desc + DoD + security remain |

Phase 4 whole-phase also requires Task Detail context panel with a visible
non-zero budget meter: verified in Edge 2026-09-10 (`Token budget: 52 / 8000`
after Compile brief). Parent stays mixed because Tree/ChangedScope is still 🟡.

## 7. Phase 5 — MCP write path

✅ stdio `beacon mcp` (`ProjectBeacon.Cli`): `read_file` / `write_file` /
`apply_patch`. Live process harness (`McpLiveProcessTests`) spawns
`dotnet exec beacon.dll mcp`: `tools/list` includes write, no shell; `../`
rejected. Path sandbox unit tests; apply_patch is all-or-nothing. Host
deny-native-tools: `.net project docs/mcp-host.md`. HTTP MCP not shipped.

## 8. Phase 6 — Labels, Decisions, Reports, coordination

| # | Item | Status | Notes |
|---|---|---|---|
| 1 | Decision/LabelPath/Report entities | ✅ | Decision+DecisionTask, LabelPath, Report. Migrations `AddReportAndLabelPath`. |
| 2 | Auto-label by path | ✅ | `AutoLabel` + `PathMatcher`; live `GET /v1/projects/{id}/labels/match` and create-task `path` |
| 3 | Shared PathMatcher | ✅ | Unit tests for `apps/api` vs `apps/api-legacy`; used by AutoLabel and LabelPath |
| 4 | Milestone orphan lint | ✅ | `ClosedAt` + `MilestoneOrphanLint.OpenTasksOnClosed`. Roadmap shows a warning. |
| 5 | Decision consequences in brief | ✅ | See Phase 4.2 |
| 6 | Labels/Decisions/Reports/Agents screens | ✅ | Edge: Reports generate/list snapshot; Decisions create+accept ("Ship DnD" → Accepted); Settings/Agents visited; Board DnD Todo→InProgress. |

## 8.5 Task pipeline and local model orchestration (M1–M6)

Companion: `task-pipeline-local-agents.md`. Flip a row to ✅ only after its
acceptance criterion was run.

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| M1 | Fundamentals (A1, A2, B1, B2) | ✅ | `dotnet build` green; migrations `AddLocalModelRegistry` and `AddTaskPipeline` apply; Domain.Tests pipeline transitions pass |
| M2 | Block A app+API+UI (A3, A5, A6) | ✅ | UI CRUD backends + role bind; API `/v1/models*` responds. Playwright on `http://127.0.0.1:5183` / `beacon_test`: add/edit/bind/unbind/delete + proxy unavailable (NFR-A3). |
| M3 | llama-swap supervisor (A4, A8) | ✅ | Fake HTTP listener: `/health` `/running` `/metrics` `/unload`; empty/seeded registry writes config.yaml; no bin/project → unavailable. `LlamaSwapSupervisorTests` 9/9. Live fake-binary smoke was run in A4. |
| M4 | Block B app+API (B3, B4) | ✅ | Handler + curl flow: start → subtasks → actor sessions → results → review → approve → task `Done` + ReviewNotes; reopen ≤ `BEACON_MAX_REOPEN_CYCLES`. `PipelineHandlerTests` covers happy path, D13, D14. |
| M5 | MCP tools (A7, B5) | ✅ | `beacon mcp` `tools/list` = 6 file + 6 pipeline/model; live Postgres; no DB → `isError`; file tools intact. `mcp-host.md` matches `McpStdioServer`. |
| M6 | UI + tests + docs (B6, B7, B8, A8) | ✅ | `dotnet test` green. `[Fact]`/`[Theory]`: Domain 128, Application 153, Infrastructure 39, API 40, Web 68 (runner cases 128/160/39/40/81; Application has 2 theories / 9 `[InlineData]`, Web has 8 / 21). Web 44→40 was a counting mismatch: 44 was expanded cases at `e33f9bf` (32 `[Fact]` + 12 `[InlineData]`); 40 was a later attribute total (38 + 2). `git log` on `ProjectBeacon.Web.Tests` keeps every test method from this note (38 then, 68 now). Playwright on `:5183`/`beacon_test`: TaskDetail happy path (start→subtask→actor→launch→result→review→approve→close) and reopen+admin force-close. `mcp-host.md` matches `McpStdioServer`. |
| M7 | MCP control plane over `/v1` | ✅ | `McpApiToolsTests`: `tools/list` includes `context_compile`, `claim_task`, `finish_work`, `list_tasks`, `list_decisions`, `list_milestones`, `generate_report`, `pipeline_start`; stub HTTP records `GET /v1/projects/{id}/tasks`, `POST …/context/compile`, `PATCH …/claim`, `POST /v1/work/finish_work`, `GET /v1/models` + proxy, `GET …/pipeline`. Missing API client → `isError`, `write_file` still works. `CoordinationHttpTests` covers decision deprecate and supersede. `mcp-host.md` matches `McpStdioServer`. |

## 9. Phase 7–9

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| C0 | Device enroll + heartbeat + long-poll | 🟡 | `POST /v1/devices` returns `bcd_` once; heartbeat + `GET /v1/devices/me/commands`; `DeviceHandlerTests` + `DeviceHttpTests` |
| C1 | `list_dir` + `ProjectRuntime` | 🟡 | Wizard picks an online device and attaches `LocalRoot` per (project, device) |
| C2 | `init_project` | 🟡 | Client writes `.gitignore` + merge `opencode.json` + `.opencode/data`; token not in git |
| C3 | MCP apply | 🟡 | Init installs beacon MCP; Agents catalog (context7, serena, custom) applies with `mcpReplace`; `McpCatalogTests` |
| C4 | Solo / Pipeline models | 🟡 | Agents UI Solo/Pipeline + Apply on device writes `opencode.json` provider/model/agent; GGUF pick from modelsRoot; `OpencodePayloadTests` + `ApplyAgentConfigHandlerTests` |
| C5 | probe + install | 🟡 | Probe in heartbeat; Settings shows tools and Install git/node/docker via winget after Confirm |
| C6 | llama-swap on client | 🟡 | Web no longer hosts llama-swap; client writes config from `/v1/devices/me/llamaswap-config` and reports status in heartbeat; Agents proxy uses device probe. `DeviceLlamaSwapProxyTests` |
| C7 | Client TUI first-run + settings | 🟡 | `beacon client` in a TTY runs a Spectre.Console walkthrough (URL, enroll or token, paths, probe/install, Windows autostart) then a live dashboard; `--headless` is the stderr loop. Reload restarts llama-swap. `ClientDaemonTests` |
| C8 | Parallel llama-swap models + host load | 🟡 | Per-backend `Concurrent` emits llama-swap `groups.resident` (`swap: false`, `persistent: true`). Heartbeat probe includes `loadedModels` + host CPU/RAM/GPU. Agents and Dashboard show load bars. `LlamaSwapConfigGeneratorTests` + `DeviceLlamaSwapProxyTests` + `ClientDaemonTests` |

| O1 | K8s blue-green Web image | 🟡 | `Dockerfile` publishes Web; `deploy/k8s` blue/green + Service switch; migrate Job; `GET /v1/version`; release workflow on `v*` tags. Client auto-update is out of scope. |

Code index, worker jobs, eval harness still ⬜. Hosted clone and `ff.sidecar_tunnel` stay off. Postgres-only `docker-compose.yml` exists for local DB. Production Web image is separate (`Dockerfile`).

## 10. Open decisions

| # | Decision | Status |
|---|---|---|
| D1 | API split from Web | 🟡 API project exists; Web maps the same `/v1` modules on `:5083` |
| D2 | xUnit | ✅ |
| D3 | `src/` layout | ⏳ Defer |
| D4 | Nav | 🟡 Shipped nine drawer items (Dashboard + Context + Decisions included). No command palette. |
| D5 | MCP after context compilation | ✅ Live stdio write path |
| D6 | Hangfire | Unstarted |
| D7 | SharpToken | ✅ Wired as `cl100k_base`; compile tests use it |

## 11. Recheck every phase

| Requirement | Status |
|---|---|
| Real rate limiting on auth entry points | ✅ |
| No hardcoded DB passwords in config | 🟡 JWT secret only in Development appsettings / `JWT__Secret` env; production appsettings has none |
| BCrypt only from Infrastructure | ✅ |
| Cross-tenant isolation proven | ✅ Fail-closed filters (`null` → empty, not all rows). `TenantIsolationPostgresTests` + `TenantFilterTests` + `NonMember_CannotListForeignProjectTasks`. Hosted multi-tenant stays off. |
| Cold-diff gate | ✅ domain + finish_work review |
| Constraint Kind structured | ✅ contradiction compile path |
| Decision Consequences | ✅ |
| Boundary-aware path matching | ✅ PathMatcher used by AutoLabel, LabelPath, create-task |
| Every ✅ has a run criterion | Enforced from this rewrite |
