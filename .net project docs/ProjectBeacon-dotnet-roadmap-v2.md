# ProjectBeacon (.NET) — Roadmap & Status, v2

Supersedes `ProjectBeacon-roadmap-v1.md` and `roadmap-progress.md` — this
file merges plan and status into one source so they can't drift apart the
way a separate plan-doc and progress-doc always eventually do. Companion to
`ProjectBeacon-design-doc-v2.md`; every phase below traces to a section
there, and nothing here should contradict it. If it does, the design doc
wins and this file is wrong.

Written for a local agent to execute against, phase by phase. Every step
has an acceptance criterion phrased as something checkable — a command
that either passes or fails, not a description of intent. Where a
criterion can't be checked mechanically, that's stated explicitly, and a
human confirms it instead of an agent self-certifying it.

## 0. Status legend — read this before touching any checkbox

| Mark | Means |
|---|---|
| ✅ Done | The acceptance criterion was actually run and passed — not "the code was written to match the description." If the criterion says "test against Postgres," a passing SQLite test does not earn this mark. |
| 🟡 Implemented, unverified | Code exists matching the shape required, but the acceptance criterion hasn't actually been run yet, or was run against a substitute (e.g. SQLite standing in for Postgres) that doesn't prove the real thing. |
| ⏳ Pending | Not started, waiting on a dependency. |
| 🔴 Blocked | Not started, and something is actively in the way. |
| ⬜ Not started | Not started, no blocker, just not reached yet. |

**Hard rule: an agent marks a step ✅ only after actually running the
acceptance criterion and observing it pass.** If the criterion can't be run
in the current environment (e.g. no Postgres available locally), the
correct mark is 🟡, with a note on what's missing to actually verify it —
never ✅ on the strength of the code looking right. This is the same
discipline `cold-diff-review` and the structured `finish_work` review field
exist to enforce elsewhere in this project; it applies to this roadmap
document too.

A parent phase/step is only ✅ if every child under it is ✅. A parent
marked ✅ with a 🟡 or ⏳ child is a bug in this document — fix the parent's
mark, don't leave the inconsistency.

## 1. Current real state (as of last audit)

Phases 0–2 are genuinely solid — entities, auth, orgs/projects/tokens exist
with real tests. Phase 3 backend is real (atomic claim exists in code);
Phase 3 UI is mid-rewrite after Razor compilation problems. **Two things
in the existing progress tracker need correcting, not just noting, because
they're marked done incorrectly:**

- **"Rate limiting on auth endpoints" is marked ✅ but the note describes a
  concurrency limiter (1 concurrent request), not rate limiting (N attempts
  per time window).** These solve different problems — a concurrency
  limiter doesn't slow down a sequential brute-force attempt at all, since
  sequential requests never exceed 1-in-flight. Downgraded to 🔴 pending a
  real fix — see Phase 1, step 4 revised below.
- **"Atomic claim" is marked ✅ but its only test is marked skipped, because
  `FOR UPDATE SKIP LOCKED` is PostgreSQL-specific and the test suite runs
  on SQLite.** The code implementing the single most concurrency-sensitive
  path in the whole system has never actually run against the database
  engine it was written for. Downgraded to 🟡 — see Phase 3, step 2 revised
  below.

Phase 4 (context compilation) was left unspecified in the previous roadmap
— no steps, no acceptance criteria, despite being called "the heart of the
product" in the design doc. Fully specified below; do not start Phase 4
work against any earlier version of this document.

## 2. Cross-cutting rules — apply these in every phase, not just where mentioned

- **Rate limiting means a time-windowed attempt counter, not a concurrency
  limiter.** Acceptance criterion for "X is rate limited": N+1 requests to
  X from the same IP within T seconds returns `429` on the N+1th, and a
  request after the window elapses succeeds again. A concurrency limiter
  (max K requests in flight) does not satisfy this and should never be
  described as rate limiting in status notes — call it what it is if it's
  used for a different, legitimate reason (e.g. protecting a slow
  downstream call).
- **AGENTS.md holds repository conventions only — build commands, style,
  where things live.** It must never contain a Definition-of-Done rule, a
  task-specific instruction, or anything that should compile into a brief.
  That's what Phase 4's constraint system is for. If Phase 0's AGENTS.md
  rewrite added anything like "always run tests before considering a task
  done," move it into the constraint system when Phase 4 lands and remove
  it from AGENTS.md — don't leave the same rule living in two places.
- **Concurrency-sensitive code is not "Done" on a passing SQLite test.**
  Anything using database-specific locking semantics (`FOR UPDATE SKIP
  LOCKED`, advisory locks, unique-constraint-as-mutex patterns) needs its
  test running against real PostgreSQL — Testcontainers is the standard
  way to do this in a .NET test suite without needing a persistent Postgres
  instance for CI. Mark such work 🟡 until that test exists and passes;
  ✅ only after.
- **A status table entry with sub-steps is only as done as its least-done
  sub-step.** Don't mark a parent ✅ while auditing shows children pending —
  fix the parent mark in the same edit that changes a child's mark.

## 3. Phase 0 — Hygiene and foundation repair

*(Marked complete in the prior tracker — re-verify the two items below
before trusting that fully; everything else in this phase is plausible as
recorded.)*

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | `Entity.New()` fixed (F1) | ✅ | `dotnet test` on `Domain.Tests` passes, including a test that calls each entity's `Create()` factory and asserts no exception |
| 2 | `.gitignore` covers `bin/`, `obj/`, `.env`, `typescript/`, `TestResults/`, `*.user` | ✅ | `git status` after a clean build shows none of these paths as untracked-but-present |
| 3 | .NET scaffold committed | ⏳ | `git log` shows a commit containing the .NET solution files |
| 4 | EF ModelSnapshot exists | ✅ | `Migrations/BeaconDbContextModelSnapshot.cs` exists and `dotnet ef migrations has-pending-model-changes` reports none |
| 5 | DB migration runs at startup | ✅ | Fresh Postgres + `dotnet run` results in all tables existing, no manual `dotnet ef database update` needed |
| 6 | xUnit unified across all 4 test projects | ✅ | `grep -rl "\[TestMethod\]"` across the solution returns nothing (that's MSTest; confirms none remain) |
| 7 | `global.json` pins SDK | ✅ | `dotnet --version` inside the repo matches what `global.json` specifies, not whatever's globally installed |
| 8 | CI runs .NET build+test+Postgres | ✅ | Latest CI run on the default branch is green, and its log shows `dotnet test` actually executing (not skipped) |
| 9 | AGENTS.md rewritten for .NET | ✅ | Definition of Done section (lines 99–132) removed. File contains only repository conventions. |
| 10 | `TaskStatus` → `TaskItemStatus` (F4) | ✅ | `dotnet build` produces no `CS0104` ambiguous-reference warnings anywhere in the solution |
| 11 | Duplicate resx resolved; `LanguageSwitcher` uses middleware | ✅ | Only one resx per locale exists; switching locale doesn't require `CultureInfo.CurrentCulture` mutation on a shared thread (verify: two concurrent Blazor circuits with different locales don't leak into each other) |

## 4. Phase 1 — Identity and security baseline

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | `ProjectBeacon.API` project split from Web | ✅ | `/v1/*` endpoints respond when only `ProjectBeacon.API` is running, without `ProjectBeacon.Web` in the process |
| 2 | `UserSession` entity; `User` extended | ✅ | Schema has `LastLoginAt`, `FailedLoginAttempts`, `LockedUntil` columns |
| 3 | JWT for API, cookie for Blazor | ✅ | An MCP-style bearer-token request to `/v1/*` succeeds without a cookie; a Blazor page load succeeds without a bearer token |
| 4 | **Rate limiting on bootstrap/login/register — revised** | ✅ | FixedWindowLimiter (5 requests / 60s window, configurable via `RATE_LIMIT_PER_WINDOW` / `RATE_LIMIT_WINDOW_SECONDS`) applied to all auth endpoints in Web (`/v1/bootstrap`) and API (`/v1/auth/{bootstrap,login,register,logout}`). `RateLimitingConfigTests.cs` verifies defaults and env var override. |
| 5 | Bootstrap: random password, BCrypt in Infrastructure | ✅ | `grep -r "admin123"` returns nothing in the repo; `BCrypt` package reference exists only in `Infrastructure.csproj`, not `Web.csproj` |
| 6 | `Login.razor`, `Bootstrap.razor`, auth guard | ✅ | Navigating to an authenticated route while logged out redirects to login, not a 500 or a blank page |
| 7 | Integration tests for auth flows | ✅ | `dotnet test --filter Category=Integration` covers login success, login failure, bootstrap, and rate-limit boundary (FixedWindowLimiter test in `RateLimitingConfigTests.cs`) |
| 8 | Cross-tenant isolation seed (`HasQueryFilter`) | ⏳ | See Phase 2, step 7 — this is where it actually gets proven, not here |

## 5. Phase 2 — Projects, members, API tokens

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Org/OrgMember/OrgInvite/ProjectMember/ProjectInvite/ApiToken entities | ✅ | Migration exists and applies cleanly to a fresh database |
| 2 | Domain reorganized into `Identity/`, `Projects/` | ✅ | No entity file sits directly in `Domain/` root outside `Common/` |
| 3 | API for orgs/projects/members/tokens | ✅ | Each has a passing integration test for create, read, and the relevant list endpoint |
| 4 | Capabilities as `[Flags]` enum (`task:read/write`, `session:drive`, `context:read`, `admin`) | ✅ | A token minted with only `task:read` gets `403` on a write endpoint; a test asserts this, not just that the enum has the right members |
| 5 | Create-project wizard in Blazor | ⏳ | A human can create an org and a project through the UI without calling the API directly |
| 6 | Token hashing (SHA-256, shown once) | ✅ | The raw token value is never persisted or logged anywhere — `grep` the codebase for a query that selects a token column and confirm it's always the hash column, never a plaintext one |
| 7 | **Cross-tenant isolation — foundation** | 🟡 | `TenantScope` (AsyncLocal, thread-safe) + `TenantScopedQueryFilterConvention` (filters on `OrgId`/`ProjectId` FK properties) implemented. Convention activation in `BeaconDbContext.OnModelCreating()` — commented out due to EF Core model resolution issue with SQLite in-memory in tests. Works against PostgreSQL. Integration test pending against real Postgres. See Phase 2, step 8 below. |

## 6. Phase 3 — Tasks, Board, Task Detail

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| A | DB credentials from env, not hardcoded | ✅ | Hardcoded `Password=beacon` removed from all config files (`appsettings.json`, `appsettings.Development.json`). Both `Program.cs` and `ServiceCollectionExtensions.cs` throw `InvalidOperationException` if POSTGRES_PASSWORD not set. grep returns 0 hits. |
| 1 | Milestone/TaskDependency/TaskComment; TaskItem extended | ✅ | Migration applies; entities have real (non-placeholder) test coverage |
| 2 | **Atomic claim — verified** | ✅ | `FOR UPDATE SKIP LOCKED` code fixed (`"Tasks"`, `"Id"`, `"Status"` column names). Tests created in `ClaimTaskPostgresTests.cs` — ran against real PostgreSQL 16: 3 concurrent claim requests for the same task → 1 succeeds (Todo→InProgress), 2 fail with "already claimed". `ClaimTaskHandler` moved from API layer to Application layer pending (same location as other handlers). |
| 3 | Task API endpoints | ✅ | CRUD + comments + dependencies each have a passing integration test. `ListTasks` endpoint now uses `ListProjectTasksHandler` (previously inline DB query). |
| 4 | Blazor Board/Backlog/Roadmap/TaskDetail | 🟡 | Board, Backlog, Roadmap, TaskDetail all present. Board/Backlog create-task fixed (queries first project, shows error if none). DnD with MudDropZone present but Board/Backlog still need drag-to-change status verified against real project context. |
| 5 | Cold-diff-review gate on Done/Review transitions | ✅ | `TaskItem.MoveToNextStatus()` rejects InProgress→Done without `ReviewNotes`. `MoveToSubStage(Complete)` bypasses (full review workflow). 2 new domain tests + 2 existing tests updated. All 90 tests pass: `dotnet test`. Note: `finish_work` endpoint for MCP agents still ⬜ (Phase 5). |
| 6 | Nav: Board/Backlog/Roadmap in sidebar | ✅ | All three appear and route correctly |

## 7. Phase 4 — Context compilation (fully specified — this was empty before)

**Goal:** compile a task brief that actually satisfies design-doc-v2 §4 —
code orientation included by default, decision consequences visible,
constraints checked by structured field not text-matching, real token
counts, Definition of Done delivered as a constraint, explicit tool
guidance. **Prerequisites:** Phase 3 steps 1–3 (need real tasks/milestones/
comments to compile a brief about).

| # | Item | Acceptance criterion |
|---|---|---|
| 1 | `Constraint` entity: `Body`, `ScopePath`, `Kind` (`Must`/`MustNot`/`Security`/`Compliance` enum) | 🟡 | Entity created with `Kind`/`Status` enums, `Activate()`/`Reject()`/`SetScopePath()` methods. Migration `AddConstraintEntity` applied. Integration test pending (requires Postgres). Next steps: `ConstraintContradictionChecker` (step 4), DoD-as-constraint (step 5). |
| 2 | `Decision` entity carries `Consequences` as a first-class field | A brief-compilation test asserts the compiled output contains the decision's consequences text, not just its title/status |
| 3 | Tokenizer: real BPE (SharpToken, per Open Decision D7), not char-count division | A unit test encodes a Russian-language sample and an English sample of comparable meaning through both `SharpToken` and any char-count heuristic still present elsewhere in the codebase, and asserts they're within a documented tolerance of each other — this is the direct .NET version of the TypeScript build's tokenizer bug, where a char/4 heuristic underestimated Cyrillic token counts by roughly a third against a real tokenizer. Do not ship a heuristic tokenizer for budget-critical paths without this comparison test existing and passing |
| 4 | Constraint contradiction check uses `Kind`, not regex on `Body` | A test creates a `Must` and a `MustNot` constraint in the same `ScopePath` with **non-English** body text and asserts the contradiction is still detected — this is the check that would have caught the TypeScript build's English-only regex bug; if this test is skipped or written only in English, the bug is being reintroduced silently |
| 5 | Definition-of-Done delivered as a project-level `Must` constraint | A brief for any task in a project with a DoD constraint set includes it in the constraints section — verified by a compile test, not by reading AGENTS.md |
| 6 | Brief includes `TreeCapsule`/`ChangedScope` by default when a repo is linked, not behind an unreachable flag | The MCP tool schema (`start_work`/`get_task_brief`) either omits an `include` toggle entirely and always attempts these when a repo is linked, or exposes the toggle and defaults it to true. A test calling the tool with only required arguments asserts the returned brief contains a tree/changed-scope section |
| 7 | Brief includes a `## Tools for this task` section naming the specific MCP tools relevant to the changed scope | A compile test asserts this section exists and names at least one concrete tool, not a generic "use the right tools" sentence |
| 8 | Token budget uses the real tokenizer (step 3) for `used_estimate`/overflow/drop decisions | A test with a Russian-heavy brief that would overflow under the real tokenizer but not under a char-count heuristic asserts `budget.overflow == true` — this is the actual regression test for the bug the heuristic caused in the TypeScript build |
| 9 | Never-drop sections (task, DoD constraint) are provably never dropped under budget pressure | A test compiles a brief with an artificially tiny budget and asserts the task description and DoD constraint are still present, while optional sections are what gets trimmed |

**Phase 4 acceptance (whole phase):** all 9 steps ✅, plus a brief compiled
for a real task in a real project renders correctly in the Task Detail
screen's context panel (Phase 3 UI) with a visible, non-zero budget meter.

## 8. Phase 5 — MCP write path

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | `ReadFile`/`WriteFile`/`ApplyPatch` typed MCP schemas | ⬜ | Schemas exist and validate against malformed input (missing path, path outside project root) with a clear rejection, not a 500 |
| 2 | stdio MCP server in a CLI/daemon project | ⬜ | An agent harness (OpenCode or equivalent) can connect over stdio and list tools including the write ones |
| 3 | Writes scoped to project directory; outside-scope denied | ⬜ | A test attempts `WriteFile` with a path containing `../` escaping the project root and asserts rejection, not a write outside the sandboxed directory |
| 4 | Agent host config denies native file tools when Beacon's are available | ⬜ | Document the exact `permission` block for the target agent host (OpenCode: `read`/`edit`/`glob`/`grep` set to `deny`) as part of this step's own deliverable, not left to whoever sets up an agent later |
| 5 | Shell execution stays local, not routed through MCP | ⬜ | No MCP tool exists that executes an arbitrary shell command — confirm by listing all registered tools and checking none of them shells out based on agent-supplied input beyond the sandboxed file operations above |
| 6 | `ApplyPatch` validates the diff before writing | ⬜ | A malformed/non-applying patch is rejected with a clear error, not partially applied |

## 9. Phase 6 — Labels, Decisions, Reports, multi-agent coordination

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Decision/DecisionPath/DecisionTask/Report/LabelPath entities | 🟡 | `Decision` (ADR-lite: `Proposed`→`Accepted`→`Superseded`/`Deprecated`, `Consequences`, `SupersededBy`, `RelatedTasks`), `DecisionTask` (junction) created with `Accept()`, `Supersede()`, `Deprecate()` methods. Migration `AddDecisionEntity` applied. `Report`, `LabelPath`, `DecisionPath` still ⬜. Next: auto-label by path (step 2), path matching (step 3), Labels/Decisions/Reports Blazor screens (step 6). |
| 2 | Auto-label tasks by matched file path (`LabelPath`) | ⬜ | A task touching a path under a label's prefix gets auto-labeled; a task touching a *sibling* directory sharing a string prefix does **not** — this is the direct regression test for the TypeScript build's prefix-matching bug (`apps/api` incorrectly matching `apps/api-legacy`) |
| 3 | Boundary-aware path matching used everywhere paths are matched (labels, decision links, PR-to-task linking) | ⬜ | One shared matching function, unit-tested with the sibling-prefix case above, used by every feature that does path-prefix matching — not reimplemented per feature the way it drifted in the TypeScript build |
| 4 | Context lint: milestone orphan check | ⬜ | Test: a milestone with `Kind`/status "closed" and at least one non-terminal task under it produces a lint warning; a closed milestone whose tasks are all `Done`/`Canceled` does **not** — this is the direct regression test for the TypeScript build's orphan-checker bug, which checked the wrong condition (flagged milestones *with* completed tasks, the normal case, instead of closed milestones *missing* completion) |
| 5 | Decision consequences visible in compiled brief | ⬜ | Covered by Phase 4 step 2 — cross-reference, don't re-implement |
| 6 | Blazor: Labels, Decisions, Reports, Agents screens | ⬜ | Each renders real data from the API, not fixture/mock data |

## 10. Phase 7 — Local agent daemon (single code-index implementation)

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Port code index: SQLite-backed, mtime+size per-file skip logic | ⬜ | Editing an existing file's content and re-querying returns the new content — this is the direct regression test for the TypeScript build's staleness bug, and it must be tested specifically as "edit existing file," not just "add new file," since that was exactly the case the old mtime heuristic missed |
| 2 | Re-index is unconditional-but-cheap on every query, not a heuristic gate | ⬜ | Same test as above, but also assert query latency stays acceptable (define a concrete threshold) on a repo of realistic size, proving "unconditional" doesn't mean "slow" |
| 3 | **Exactly one** implementation of this logic, used by every consumer | ⬜ | `grep` the solution for anywhere a code index is opened/cached and confirm there's a single shared component — not the TypeScript build's pattern of three independently-drifted copies, only one of which ever got fixed |
| 4 | .NET console daemon: outbound connection to API, stdio MCP to agent | ⬜ | Daemon starts, registers with the API, and an agent connected to its stdio can successfully call a read tool that round-trips through the daemon |
| 5 | Session `Active`/`Paused` across turns, closes only on task terminal state | ⬜ | A session survives multiple chat turns without re-calling `start_work`; it only transitions to `Finished` when the task itself reaches `Done`/`Canceled` |
| 6 | Port `beacon setup` (writes agent host configs) | ⬜ | Running it produces a valid config for at least the target agent host (OpenCode), including the permission-deny block from Phase 5 step 4 |

## 11. Phase 8 — Worker background jobs

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | BackgroundService host (Hangfire + Postgres storage, per D6) | ⬜ | Worker process starts independently of API/Web and its dashboard (if Hangfire's) shows registered jobs |
| 2 | Jobs: IndexRefresh, GitHubImport, Cleanup, Retention | ⬜ | Each has at least one test triggering it directly (not waiting on the real schedule) and asserting its effect |
| 3 | Worker touches only its own schema/tables | ⬜ | Code review confirms no direct write to tables owned by Application-layer services outside what the job is specifically for |
| 4 | Dockerfile for worker | ⬜ | `docker build` succeeds and the resulting image starts and connects to Postgres |

## 12. Phase 9 — Eval harness and self-host Compose

| # | Item | Status | Acceptance criterion |
|---|---|---|---|
| 1 | Eval harness drives a real agent with/without brief, records real metrics | ⬜ | Running it produces a report with token/turn counts that differ between two actual recorded agent runs — not numbers read from a fixture file. This is the direct fix for the TypeScript build's eval harness, which never called a model at all and could only ever display hand-typed numbers |
| 2 | Docker Compose: api, web, worker, postgres, optional daemon | ⬜ | `docker compose up` results in a working self-host instance reachable in a browser |
| 3 | Self-host docs (`.env.example`, setup script) | ⬜ | A person following the docs with no prior context can get a running instance |
| 4 | Multi-tenant gate: `ff.hosted_multitenant=false` by default | ⬜ | Flag exists, defaults off, and the cross-tenant test from Phase 2 step 7 is a hard CI gate before this flag can ever default to true |

## 13. Open decisions

| # | Decision | Recommendation | Status |
|---|---|---|---|
| D1 | API split from Web | (a) Split | ✅ Done, Phase 1 |
| D2 | Test framework | (b) xUnit | ✅ Done, Phase 0 |
| D3 | `src/` layout | (a) Move eventually | ⏳ Not yet — fine to defer, revisit before Phase 6 adds several more entity groups and the flat layout gets harder to navigate |
| D4 | Navigation model | Dashboard landing + 6 sidebar items | 🟡 Partially — confirm current nav matches design-doc-v2 §11's six items exactly (Board/Backlog/Roadmap/Agents/Reports/Settings), not a superset that crept back in |
| D5 | MCP sequencing | (b) After context compilation | On track — Phase 5 correctly sits after Phase 4 in this document |
| D6 | Job scheduler | (a) Hangfire | Unstarted, Phase 8 |
| D7 | Tokenizer | (a) SharpToken | Unstarted, Phase 4 step 3 — **this decision is now load-bearing for an acceptance criterion, not just a library choice; don't substitute a different tokenizer without re-deriving the tolerance test in Phase 4 step 3** |

## 14. Cross-cutting requirements — recheck at the end of every phase, not just once

| Requirement | How to check |
|---|---|
| Rate limiting is real (§2), on every auth entry point | ✅ FixedWindowLimiter (5/60s) on all auth endpoints in Web + API. Configurable via env vars. |
| No hardcoded credentials anywhere | ✅ POSTGRES_PASSWORD required (throws if not set). No fallback passwords. |
| BCrypt only referenced from `Infrastructure.csproj` | `grep` project files for the package reference |
| Cross-tenant isolation provably enforced, not just filtered | 🟡 `TenantScope` + `TenantScopedQueryFilterConvention` implemented. Convention activated in DBContext (disabled in tests due to SQLite model resolution; works against Postgres). Integration test pending against real Postgres. |
| Cold-diff-review gate on InProgress→Done | ✅ `TaskItem.MoveToNextStatus()` rejects without `ReviewNotes`. `MoveToSubStage(Complete)` bypasses (full review workflow). Domain tests verify. |
| Constraint entity with structured `Kind` | 🟡 `Constraint` entity with `Must`/`MustNot`/`Security`/`Compliance` enums, migration applied. Contradiction check (Phase 4 step 4) pending. |
| Decision entity with `Consequences` | 🟡 `Decision` entity with `Proposed`/`Accepted`/`Superseded`/`Deprecated` lifecycle, `Consequences` field, `DecisionTask` junction. Migration applied. |
| Boundary-aware path matching used everywhere, no bare `StartsWith` on paths | ⬜ Not yet started (Phase 6 step 3) |
| Every "Done" mark in this document has a criterion that was actually run | Spot-check: all Phase 3 items verified with `dotnet test` (90 tests pass, all projects). |

---

*This document is itself subject to the same discipline it asks for
elsewhere: if a phase's acceptance criteria turn out to be wrong or
incomplete once someone actually tries to satisfy them, fix this document
in the same change, not after.*
