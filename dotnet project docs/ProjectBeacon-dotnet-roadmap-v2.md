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

## 9. Phase 7–9

⬜ Local daemon / code index, worker jobs, eval harness. Postgres-only
`docker-compose.yml` exists (not full product Compose). Not this slice.

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
