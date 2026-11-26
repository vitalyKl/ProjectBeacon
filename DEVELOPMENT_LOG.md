# ProjectBeacon — Release Management Development Log

**Implementation branch:** `feat/release-management`  
**Plan branch:** `docs/release-management-plan`  
**Initial base:** `main` at `22a1a9e559a97124f939b6da2de2e4f9612f4d94`  
**Current PR base (25 Nov 2026):** `main` at `90a1778359ae0af5efd622af2876159e31c3480b` (`0.7.5.9`)  
**Started:** 2026-11-24  
**Scope:** universal ProjectBeacon product feature for customer projects; no SaaS Control Plane, billing, or platform-operator work.

This log is maintained in the repository and updated in the same branch as implementation. It is the execution record; the plan remains the requirements baseline.

## Status

- Overall: **In progress**
- Current slice: **final verification and closure of S0–S6**
- S0 AutoSync, S1 Version Policy/task impact, S2 Release/Roadmap, S3 Candidate/Prepare/Verify/Publish, S4 GitHub awareness/reconciliation, S5 Hotfix and S6 Planner/onboarding code paths are present in this branch
- **No slice is accepted as complete until the exact current head has green CI and the remaining security/authority blockers are resolved**

## Initial repository findings

| Area | Current finding | Consequence |
|---|---|---|
| Task Git/GitHub operations | PR #194 is already in main; safe task-branch publishing and PR status/actions exist. Direct PR merge is not part of that contract. | Extend existing workflow; do not rebuild publishing. |
| Workstation pin | PR #227 is already in main. Task branch workspace and review/landing proof use a persistent task workstation pin. | Original plan's station-pin uncertainty is resolved. |
| GitSync queue lifecycle | PR #228 is already in main. Pending GitSync commands have lifecycle/expiry/cancellation handling. | AutoSync must avoid queue flooding and preserve the existing cancellation contract. |
| AutoSync | Existing `feat/git-autosync` branch is identical to main. Current main tree has no dedicated periodic AutoSync loop or the referenced `claude/prompt-git-autosync.md` file. | Implement as an actual safe, bounded background behavior; do not treat a prompt or branch name as completion. |
| Git tags | GitSync fetch uses `--no-tags`. | Define and implement tag sync separately from working-tree fast-forward. |
| Version policy | The implementation branch now has project-scoped VersionPolicy and Release entities; current main base is `0.7.5.9`. | Keep all changes isolated in this feature branch; confirm CI and migrations before merge. |
| Version workflow | Release workflow and configured version schemes are being reconciled as part of S3/S4; current repository version is `0.7.5.9`. | Do not allow tag/deploy publication until the workflow validates the configured scheme and source projections. |
| Landing authority | PR #194 says GitLand remains the landing executor and does not merge remote PRs. | Preserve current behavior until an explicit, tested remote-authority design is implemented; this remains an architecture decision. |
| GitHub protection | Tag rulesets and production environment reviewers have not yet been verified. | Do not assume dangerous publication is externally protected. Verify before S3. |

## Progress

### 2026-11-24 — Repository setup
- [x] Read the owner's plan from the conversation attachment.
- [x] Read repository `AGENTS.md`, version properties, release workflow, CI workflow, GitSync service, GitSync handlers/preflight, task GitHub operations, Milestone model, Roadmap page, and master roadmap.
- [x] Confirm the main head and compare the existing AutoSync / publishing branches.
- [x] Confirmed the station pin and pending GitSync lifecycle changes are already in main.
- [x] Added the original plan plus a dated repository-state addendum to the dedicated plan branch.
- [x] Created the implementation branch from the plan branch so it inherits the plan.
- [ ] Finish verifying GitHub branch/tag rules, environment reviewers, CI permissions/secrets and exact AutoSync loop integration points.
- [ ] Implement and validate S0.
- [ ] Implement S1 version policy, agent tools, reviewer confirmation, file protection and project UI.
- [ ] Continue S2–S6 in the order and boundaries defined by the plan.

## Next actions

1. Finish the current CI matrix, correct its compiler/test failures, and trigger another run on the exact current branch head.
2. Confirm EF model snapshot has no pending model changes and all application/API/CLI/web/worker suites pass on Linux, Windows and macOS.
3. Complete the GitHub merge-authority path: remote landing reconciliation is read-only today, and GitHub PRs still require an external merge. The agent must be able to request/execute a validated merge through a server-authorized, policy-checked operation without bypassing branch protection.
4. Verify GitHub tag rulesets and production Environment reviewers before enabling real publication.

## Blockers and decisions

- **Landing authority:** current PR #194 contract and plan recommendation differ. No code change may make GitHub and local GitLand competing authorities. Keep existing GitLand semantics until a single remote-authoritative transaction is designed and tested.
- **GitHub protection:** repository rulesets API returned an empty list on 2026-11-24. The legacy tag-protection and production Environment endpoints are not readable through the current GitHub connection (HTTP 400), and branch-protection read returned HTTP 403. This does not prove the settings are absent, but they are unverified; tag publication/deployment remains disabled.
- **Validation environment:** the current execution environment has no .NET SDK and cannot resolve github.com from the shell. GitHub connector writes are available, but local build/test execution is not. Before claiming validation, use GitHub Actions on the exact branch head or a working .NET-enabled environment.
- **ProjectBeacon MCP / Context7:** repository `AGENTS.md` requires both for project implementation. They are not exposed in this execution environment; this limitation must be reported and repository source remains the fallback, not represented as MCP/Context7 verification.



## 2026-11-24 — S0 AutoSync implementation checkpoint

**Branch head before this journal update:** `54fa197aebfe87c88038f077ded3e728545443eb`

### Implemented in `feat/release-management`

- Added `autoSyncEnabled` to the existing per-project Git policy JSON. It defaults to `false`; legacy JSON without the field remains valid and disabled. The setting participates in normal policy revisioning and audit details. No schema migration was needed.
- Added Owner/Admin project settings UI and English, German, Russian, Japanese, and Chinese resource strings. Enabling is unavailable unless an online project workstation advertises AutoSync support; an enabled value can still be turned off when that workstation is offline.
- Added `git-auto-sync-v1` workstation capability and exposed it in runtime summaries.
- Added device-only `GET /v1/devices/me/auto-sync-targets`. It returns only project roots bound to the authenticated device, only for projects whose AutoSync policy is enabled, and only when that device explicitly advertises AutoSync support. Revoked/unknown devices are rejected. It does not accept a project ID or local path from the client.
- Added `DaemonAutoSyncLoop`, registered independently from `DaemonCommandLoop`; it polls every two minutes and does not enqueue periodic workstation commands.
- Reuses the existing safe GitSync fast-forward path for the tracked upstream branch. Dirty/diverged/detached/no-upstream repositories and active task sessions are not forcibly rewritten. AutoSync does not advance task-branch snapshots.
- Added separate tag fetching with an explicit `refs/tags/*:refs/tags/*` refspec, `--no-tags`, no force marker, and no prune. New remote tags are fetched, existing local tags cannot be force-moved, and local tags are never deleted by AutoSync.
- Added tests for policy defaults/round-trip/validation, device-scoped target selection, revoked-device rejection, device-only endpoint access, new-tag fetch with a dirty working tree, and refusal to move an existing local tag.

### Checks and validation

- **Local .NET build/tests:** not run. `dotnet` SDK is not installed in this execution environment.
- **Git tag refspec experiment:** passed on Git 2.47.3 using a local bare remote. A new remote tag was fetched; after moving that remote tag, a second non-forced fetch exited with `would clobber existing tag`, and the local tag SHA remained unchanged. This validates Git's refspec semantics, not the C# implementation/test suite.
- **GitHub Actions:** pending. The repository CI workflow runs for `pull_request` events `opened`, `reopened`, and `ready_for_review`, but deliberately does not run for `synchronize`; the branch needs a fresh PR event after code changes to obtain a CI run.
- **ProjectBeacon MCP / Context7:** still unavailable in this execution environment; implementation was checked against repository source only.

### Remaining S0 work

- [ ] Open a draft PR to trigger the configured CI on the current branch head.
- [ ] Fix all CI/build/test failures and re-trigger CI using a supported PR event.
- [ ] Review the actual API/Razor integration once CI confirms compilation.
- [ ] Add any missing regression tests discovered by CI.
- [ ] Mark S0 complete only after a green CI run on the exact final branch head.

### Next slices

1. Finish S0 verification.
2. Start S1: `VersionPolicy`, deterministic `version_check`/`version_impact` tools, Reviewer support, protected version-file writes, project settings/UI/localization/RLS/tests, and a project-scoped feature flag.
3. Preserve the open landing-authority decision: current PR #194 contract keeps GitLand as landing executor, while the plan prefers GitHub as remote authority. Do not implement two competing merge authorities before this is resolved.
4. Before S3 publication, reconcile the current four-component `0.7.5.8` version with the release workflow's current three-component version validator, and verify GitHub tag protection / production environment reviewers.



## 2026-11-24 — S1 Version Policy and task version impact implementation checkpoint

**Branch head before this journal update:** `92626421094bb6fe19f7f5435e12b9b607ba47ce`  
**Branch relation to main:** 128 commits ahead, 0 behind at this checkpoint.  
**Draft PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230).

### Implemented

- Added project-owned `VersionPolicyJson`, concurrency revision, and authoritative `OfficialVersion` state to the existing Project entity. Ordinary settings can initialize the official version once but cannot change it afterwards; a future server-authorized ReleasePrepare will use the dedicated release setter.
- Added deterministic policy parsing/validation for `NumericComponents` (1–8 components), `SemVer`, and `Calendar`, with safe tag patterns and project-relative version source paths. XML/XPath, JSON Pointer (including array indexes), dotted TOML key, and Regex selectors are supported.
- Added task-owned `VersionImpact`, rationale, timestamp, and concurrency revision. A task title/description scope change clears stale impact and its Reviewer decision.
- Added Reviewer decision state (`Confirmed`/`Challenged`, note, timestamp, Review session id). A decision is bound server-side to the currently active Review pipeline session for the exact task/project and to the current impact revision.
- Added project version-policy GET/PUT handlers and task impact GET/PUT/review handlers, with human Owner/Admin checks, project/token scope checks, optimistic concurrency, and audit events for declarations and Reviewer decisions.
- Added API routes and role-enforced MCP tools: `version_check`, `release_context`, `get_task_version_impact`, `version_impact`, and `review_version_impact`.
- Added deterministic `version_check` file comparison in the isolated workspace. It reports the Beacon official version, policy revision, tag name, and per-file projection state; it explicitly does not claim tag or deployment verification.
- Added ordinary MCP `write_file`/`apply_patch` protection for configured version-source files. The protection lookup fails closed when policy cannot be verified. Landing proof independently unions configured version-source paths into the protected path snapshot, so policy edits do not silently change an existing landing attempt's protection.
- Added Owner/Admin project settings UI for scheme, component count, calendar format, prerelease handling, tag pattern, initial official version, and version-source selectors.
- Added English, German, Russian, Japanese, and Chinese resource strings for the complete settings form.
- Added role-specific Actor and Reviewer instructions in session prompts and the native-agent system prompt.
- Added migrations and model snapshot updates for project policy/version state and task impact/review state.

### Tests added (pending CI verification)

- Version catalog tests: 4-component numeric versions, SemVer prerelease policy, calendar format, XML/JSON/TOML/Regex extraction, JSON Pointer arrays, path escape rejection, unknown policy property rejection, round-trip, invalid Git tag syntax.
- Domain task tests: validation of impact/rationale, Reviewer confirmation/challenge, clearing stale review after impact or task-scope change.
- API authorization tests: explicit Actor tool grants, Planner/Reviewer read-only version context, Reviewer-only impact confirmation route.
- MCP tests: projection comparison against the official version and exact Actor/Reviewer request routes/revisions.

### S0 CI result and fixes

- First CI run: [run #45378007002](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45378007002), head `d2e3f786f88bd718a1fcef392c310ae0bd725a6f`.
- Linux, Windows and macOS builds passed; Linux and macOS EF snapshot checks passed. Linux tests reported two failures:
  1. The new `DeviceAutoSyncTargetsHandler.cs` was absent from `docs/ignore-query-filters.allowlist`.
  2. The new-tag test tried to delete a tag that was never implicitly fetched into the working clone.
- Both were fixed. The allowlist now records the actual three explicit `IgnoreQueryFilters()` call sites (device, runtime, project join); the tag test no longer deletes a nonexistent local tag.
- The run above is **still a failing run on the pre-fix head**. No green CI run exists yet for the current branch head.

### Remaining work / blockers

- [ ] Run CI on the current branch head and repair any failures before marking S0/S1 verified.
- [ ] Add any tests that CI or review reveals are missing, especially API integration tests for the project policy and Reviewer session handler.
- [ ] Add the separate Release entity and milestone association in S2; it is intentionally not conflated with the existing Milestone.
- [ ] Resolve the GitHub remote authority versus GitLand landing executor design before S2/S3; do not create two merge authorities.
- [ ] Verify GitHub tag protection/rulesets and required reviewers on the `production` Environment before publication work.
- [ ] Fix the release workflow's current three-component validator before S3; current Beacon version is four-component `0.7.5.8`.

### Validation limitations

- Local .NET build/test execution remains unavailable because this environment has no .NET SDK.
- ProjectBeacon MCP and Context7 are not exposed in this execution environment. Source-level review and GitHub Actions are the available verification path.
- The branch remains isolated; PR #230 is not merged and main is unchanged.



## 2026-11-24 — S2 Release/Roadmap and Version Policy API correction

**Current implementation branch at the time of this update:** `feat/release-management`.

### S2 — Release and Roadmap product slice now present

- Added a separate project-scoped `Release` entity and kept `Milestone` independent with optional `ReleaseId`.
- Added composite FK `(Milestone.ProjectId, Milestone.ReleaseId) -> (Release.ProjectId, Release.Id)`, preventing cross-project milestone attachment.
- Added Releases table with forced project RLS and raw-SQL RLS isolation tests.
- Added Release API/handlers, project settings feature flag `ReleaseManagementEnabled` (default off), Owner/Admin write controls, project-member / scoped TaskRead read access, audit events and optimistic revision checks.
- Added Roadmap UI for creating and managing Releases and linking Milestones, with localized Candidate guidance in en/de/ru/ja/zh.
- Added tests for release CRUD/authorization, feature-flag default/enforcement, inability to disable the flag while active Releases exist, cross-project FK/RLS isolation, and role-scoped MCP `release_context`.

### Candidate lifecycle hardening

- Removed manual transition to `Candidate` from generic status API and Roadmap UI. Candidate creation is reserved for ReleasePrepare.
- Candidate cannot return to editable `InProgress`; Candidate/Published releases are immutable; milestones in those releases cannot be detached or moved.
- Updated domain and handler tests for the restricted transition graph.
- **Still missing:** immutable Candidate snapshot entity and ReleasePrepare executor/proof. The Candidate lifecycle is reserved but no supported command can create a Candidate yet; that is S3 work.

### Version Policy feature-flag HTTP defect fixed

- Found that `ProjectManage.razor` sent `releaseManagementEnabled`, but `ProjectEndpoints.SetVersionPolicy` dropped it when mapping `SetVersionPolicyBody` to `ProjectVersionPolicy`.
- Fixed the endpoint mapping and added an optional `ReleaseManagementEnabled = false` body field for backward compatibility.
- Added `OrgProjectTaskHttpTests.VersionPolicyHttp_RoundTripsReleaseManagementFeatureFlag`: verifies default-off state, enabling the flag while initializing `0.7.5.9`, revision increments, then disabling it.
- Version-policy audit payload now includes the `releaseManagementEnabled` value.

### CI record after S2 additions

- Run [#45390404987](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45390404987) was for an earlier head (`1e875c193007a03472368f2b0e1c2461e94117c4`) and was later cancelled while the PR branch continued changing. It is **not a pass** and is not a definitive failure for the current head.
- The Linux log from that interrupted run contains cancellation and zero-test-run outcomes; it does not provide a reliable final test result for the branch.
- A fresh CI run must be triggered and allowed to finish on the current head before S0–S2 can be accepted.

### Current blockers / next work

- [ ] Green Linux/Windows/macOS CI on the current branch head.
- [ ] S3: immutable Release Candidate snapshot + deterministic ReleasePrepare validation and execution.
- [ ] S3: write configured version projections and `.beacon/release.json` only through ReleasePrepare, add `Beacon-Release: <id>` commit trailer, verify remote candidate SHA, and create tags only through the gated publication path.
- [ ] Resolve remote merge authority before connecting ReleasePrepare to GitHub publishing: PR #194 currently retains GitLand as landing executor, while the plan recommends GitHub remote authority. Do not create competing merge authorities.
- [ ] Verify GitHub tag protection/rulesets and required reviewers on the `production` Environment before enabling tag/deploy publication.
- [ ] Reconcile release workflow's current three-component version validator with configured schemes and the repository's four-component version `0.7.5.9`.

No changes to SaaS Control Plane or billing. PR #230 remains open and unmerged.



## 2026-11-24 — S3 ReleasePrepare implementation and CI repair checkpoint

**Branch head before this journal update:** 532861b90a436cd5d0709b5b08cd4c2602ac9cc4  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), open, ready for review, unmerged.  
**Current base:** main at 1290eae570d48c79f8523222b3293c5b536de99f.

### S2 and S3 implementation now present on the branch

- S2 adds project-scoped Release records distinct from Milestone, optional Milestone.ReleaseId association with a composite same-project FK, Release RLS, feature-gated API access, Owner/Admin UI and Roadmap integration.
- Candidate is no longer a generic/manual status transition. Candidate is created only after ReleasePrepare and VerifyLanding validation.
- S3 adds an immutable ReleaseCandidateSnapshot row carrying candidate/version/target branch, base/prepared/candidate/tree SHAs, policy and release revisions, manifest SHA-256 and bounded snapshot JSON.
- Added a server-side Prepare handler that resolves project policy, verifies the project/release/branch/version/manifest, requires a pinned ReleasePrepare-capable workstation, refuses to start alongside other repository work, stores an immutable request payload and queues a typed ReleasePrepare workstation command.
- Added a VerifyLanding handler bound to the same pinned workstation and stored Prepare evidence; it checks revision/candidate/manifest/commit evidence before Candidate transition.
- Added client-side ReleasePrepare executor and command completion handling for Prepare/VerifyLanding.
- Version-policy settings are now blocked while a ReleasePrepare workflow is active, preventing version projection rules from changing between Prepare and VerifyLanding.
- ReleasePreparePayload.ToCommandDto now reuses EnqueueCommandHandler.MapCommand, avoiding a second hand-maintained WorkstationCommandDto mapper.

### CI findings from the last completed pre-fix run

Run [#45409649488](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45409649488) tested an earlier head (5ed5a18678923819fddfe632844fd4c07d3e82cd), not the current branch head. It failed due to:
1. CS8510 unreachable duplicate /releases switch arm in the MCP test RecordingHandler.
2. EF model snapshot failure on Release.CandidateSnapshot navigation because the snapshot model represents Release as a shared dictionary entity.
3. Stale test assumptions after adding ReleasePrepare: TenantFilter expected 33 scoped entities instead of 35; command delivery expected 25 kinds instead of 26; WorkstationCommandCatalog did not list ReleasePrepare among project-runtime commands.
4. PolicyCannotChangeOfficialVersionAfterInitialization read the Project through the test fixture's already-tracked context, therefore it saw stale null state after a separate handler unit-of-work persisted the official version.

### Fixes now committed on the branch

- Removed the duplicate /releases switch arm and consolidated the fixture response to a single InProgress Release payload.
- Made ReleaseCandidateSnapshot.Release the EF dependent-side navigation; Release.CandidateSnapshot remains an in-memory aggregate reference but is explicitly ignored by EF. The model snapshot no longer configures a principal navigation on the shared Release entity. The unique (ProjectId, ReleaseId) relationship and FK remain.
- Added ReleasePrepare to the expected project-runtime command scope.
- Updated tenant-filter baseline to 35, explicitly asserting both Release and ReleaseCandidateSnapshot are project-scoped.
- Updated command delivery baseline to 26 command kinds / 18 idempotent, and explicitly classified ReleasePrepare as idempotent and not reconcile-backed.
- Changed the official-version persistence test to read through a fresh context, verifying database state rather than a stale tracked entity.

### Validation state

- The fixes above are committed but **not yet validated by CI**.
- Run #45409649488 remains a failed historical run on an older SHA; it must not be reported as the current result.
- There is no green CI run on the exact current branch head yet.
- Local .NET SDK remains unavailable in this execution environment.
- GitHub tag protection/rulesets and required reviewers on the production Environment remain unverified. No tag or production deployment is being triggered.

### Remaining blockers

- [ ] Green Linux/Windows/macOS CI on the exact branch head.
- [ ] Review ReleasePrepareExecutor Git operations and confirm its remote landing authority is consistent with PR #194 and the owner's decision; do not merge through both GitLand and GitHub.
- [ ] Validate migration snapshot and RLS tests after the one-sided CandidateSnapshot relationship change.
- [ ] Finish publication path only after repository rulesets and production environment reviewers are verified.
- [ ] Verify the release workflow handles NumericComponents (including four-part versions) and other configured schemes without hard-coded SemVer assumptions.




## 2026-11-24 — S4 GitHub Awareness / forge_read implementation checkpoint

**Branch head before this journal update:** `125fdc38e2ecc02be9c64a2f44bf1e7708f7c946`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), open, ready for review, unmerged.  
**PR base:** `main` at `1290eae570d48c79f8523222b3293c5b536de99f`; branch is 338 commits ahead and 0 behind at this checkpoint.

### Implemented

- Added native structured Git tool `forge_read` for Planner, Actor, and Reviewer roles. It is read-only and does not reserve/exclusively lock the task worktree.
- Tool arguments are limited to `view` (`all`, `pull_request`, `reviews`, `workflows`) and a bounded `limit` (1–10, default 6). It accepts no repository, remote, branch, URL, or PR number from the model.
- Repository identity and task branch come from the daemon-authorized persistent task worktree. The tool validates the GitHub.com origin and same-repository push URL, rejects mirror remotes, authenticates through the workstation's existing GitHub CLI, and uses the daemon-selected review base.
- Reads the task branch's PR summary and required checks, PR reviews, conversation comments, inline review comments, and GitHub Actions run summaries. It re-reads the PR snapshot before returning to avoid combining results from different PR heads/review states.
- Returns bounded external content: up to 10 items per collection, comment bodies capped at 800 characters, author, URL and source ID retained, inline file/line retained, and diff hunks excluded. Actions results are summaries only; no logs or arbitrary artifacts are returned.
- The response explicitly marks GitHub content as untrusted evidence, not instructions or authorization.
- Updated native-agent instructions: Planner should turn actionable external review findings into Beacon subtasks/task notes with original author/link; Reviewer should consider them in review. No instruction asks the user to paste GitHub comments into chat.
- Updated `docs/mcp-host.md` and added role, schema, parser, truncation, malformed-response and non-exclusive execution tests.

### Validation

- CI has **not** run on the exact S4 head `125fdc38e2ecc02be9c64a2f44bf1e7708f7c946`; GitHub reports no workflow run or status for this SHA.
- Local .NET build/tests cannot run because `dotnet` is not installed in the current execution environment.
- ProjectBeacon MCP and Context7 tools are not exposed in this environment. Repository code and documented conventions were used as the fallback; they were not represented as MCP/Context7 verification.
- The only prior CI runs were interrupted or targeted older heads, so none is a passing validation for the current branch.

### Publication safety

- Repository rulesets API returned `[]` at this checkpoint.
- The legacy tag-protection endpoint and `production` Environment endpoint were unavailable through the current GitHub connector (HTTP 400); reading branch protection returned HTTP 403.
- Therefore existing tag protection and required production reviewers remain unverified. No release tag is being pushed and no production workflow is being triggered.
- The release tag workflow remains a separate human-confirmed operation; `forge_read` cannot create or modify tags, PRs, comments, workflows or remote refs.

### Remaining work

- [ ] Obtain a green Linux/Windows/macOS CI run on the exact final branch head for all current slices.
- [ ] Fix every compile/test/formatting issue from that run and re-trigger CI using a supported PR event.
- [ ] Confirm review comments and workflow summaries against a real workstation `gh` session in an integration environment; no GitHub write path is part of this tool.
- [ ] Complete S5 hotfix-from-deployed-SHA and forward-port task workflow.
- [ ] Complete S6 release-aware Planner mode, onboarding and a second version scheme proof project.
- [ ] Keep tag/deploy publication fail-closed until tag protection and production Environment reviewers are verified.



## 2026-11-24 — S3 publication and S5 Hotfix / forward-port implementation checkpoint

**Branch head before this journal update:** `c0aea83d1a12b7aaa14b4cf075b8717ca21af614`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), open, ready for review, unmerged.  
**Base:** `main` at `1290eae570d48c79f8523222b3293c5b536de99f`.

### S3 — Publication workflow added after the earlier ReleasePrepare checkpoint

- Added a separate explicit PublishRelease command and server-side handler, with an explicit human confirmation boundary.
- Added workstation capability negotiation; publication is advertised only when the client can use the supported GitHub CLI.
- Added a bounded publication executor that validates the immutable annotated tag identity and exact candidate SHA before pushing. It does not rewrite or force-update existing tags.
- Added workflow-run observation and deployment verification. The publication result is only considered successful when the GitHub release workflow and configured deployment version endpoint report the expected version and commit SHA.
- Publication evidence is stored separately from Candidate state (tag pushed, workflow run id/url, deployed version/SHA/time). Candidate does not by itself mean Published or deployed.
- No tag or production deployment was triggered by this work.

### S5 — Hotfix from deployed production SHA and forward-port lineage

- Added immutable task lineage fields for source Release, deployed SHA, target branch, Hotfix request id, and forward-port origin task, with project-scoped composite relationships and filtered unique indexes.
- Added an authenticated Release Hotfix request handler/API and project Roadmap action to create Hotfix tasks from Published releases.
- Added a device-only workspace bootstrap endpoint so a Hotfix workspace is based on the verified currently deployed production SHA, not the ordinary task base.
- Added client bootstrap flow and a second production-SHA check immediately before Hotfix worktree start. If production advanced after the request was created, the workspace is not started from a stale deployed SHA.
- Added forward-port task creation only after the Hotfix landing is verified. The Hotfix remains targeted at its deployed production branch; forward-port is a separate ordinary task that follows the normal review/landing workflow.
- Added Hotfix origin and forward-port lineage to task details and task lists, localized in en/de/ru/ja/zh.
- Added tests for deployed-SHA bootstrap, production advancing before start, Hotfix authorization, and forward-port creation only after verified landing.

### S4 — GitHub awareness

- The earlier `forge_read` implementation checkpoint remains in effect: read-only PR/review/workflow evidence, bounded output, current workstation's task checkout identity, and explicit untrusted-evidence treatment.
- No GitHub write or merge authority was added to `forge_read`.

### CI run #45429491586 — findings on older head

Run [#45429491586](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45429491586) tested head `00a50a2f081e0dec87e61e023a74d5cb966d8e30`, not the current branch head. All three platform builds completed; the run failed during EF migration verification and tests.

Confirmed failures:
1. **EF pending model changes:** `dotnet ef migrations has-pending-model-changes` reported model changes after build. PostgreSQL-backed API/Web test initializers then failed at `Database.Migrate()` with `PendingModelChangesWarning`; these are cascading migration-model failures, not separate product failures.
2. **forge_read unsupported collection:** `ParseForgeCollection("[]", "unsupported", 5)` returned an empty array because unknown collection names were not rejected before iterating the JSON array.
3. **Calendar version test:** policy validation rejected the test tag pattern `release-{version}` because it required a literal leading `v`, although the project policy is intended to own the tag format.
4. **MCP test discovery:** `VersionImpact_UsesPinnedPipelineTaskAndExpectedRevision` was missing `[Fact]`, so xUnit did not discover it.

### Fixes committed after that run

- Added an explicit allowlist for the four supported `forge_read` collections before JSON parsing.
- Removed the hard-coded leading-`v` requirement. The default remains `v{version}`, but customer projects may configure another valid Git tag pattern such as `release-{version}`.
- Added the missing `[Fact]` attribute to the Actor version-impact MCP test.
- Corrected the Hotfix/forward-port partial-index filter strings in configuration, migration and EF model snapshot to use the exact quoted PostgreSQL column identifiers. Current source inspection shows these strings aligned; the new CI run must confirm that the pending-model check now passes.

### Current validation state

- The run above remains a failed historical run on head `00a50a2f`; it is not the result for the current branch.
- No green CI run exists on the exact current branch head yet.
- PostgreSQL integration tests are run on the Linux job; the Windows/macOS test jobs report them as skipped when `BEACON_TEST_SKIP_POSTGRES` is set.
- Local .NET SDK is still unavailable in this execution environment.

### Remaining work

- [ ] Run CI on the current branch head and fix every remaining failure.
- [ ] Complete S6: release-aware Planner mode, user-facing onboarding/productization, and an independent project proving a second version scheme (for example package.json) end-to-end.
- [ ] Resolve/record the final remote landing authority decision and verify ReleasePrepare/landing do not create competing GitLand and GitHub merge authorities.
- [ ] Re-verify GitHub tag protection and production Environment reviewers before any user-authorized publication test.
- [ ] Do not mark S0–S5 complete or merge PR #230 until the exact head has green required CI and the above review blockers are resolved.

No SaaS Control Plane or billing changes. Main remains unchanged by this implementation branch.



## 2026-11-24 — S6 release-aware Planner and onboarding checkpoint

**Branch head before this journal update:** `331ea5ef754f421beebfbf032726c43798c109ad`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), open, unmerged.

### Release-aware Planner
- Extended read-only `release_context` to fetch the exact active task, verify it belongs to the requested project, and resolve its release only through the persisted `Task.MilestoneId -> Milestone.ReleaseId -> Release.Id` association.
- Context now includes `activeTask`, `activeTaskRelease`, `activeTaskReleaseAssignment` and `activeTaskReleaseMode`, plus the existing policy, release/milestone collection and task version impact.
- Actor authorization allows the exact active task detail read only when `release_context` is granted. Planner and Reviewer continue using their pinned task read-only policy; arbitrary task ids and ungranted Actor reads remain denied.
- Planner and PlannerWake prompts now explicitly enter release-aware planning only when the task is linked to a Release through a Milestone. They preserve `targetVersion`, `targetBranch` and milestone scope; Planned/InProgress may accept work. Candidate/Published/Cancelled are immutable and must not receive Actors that change the release scope. Unassigned tasks remain ordinary work; Planner does not invent associations.
- Native agent system prompt repeats the same release-assignment and immutable-state boundaries.
- Added MCP integration coverage for resolving an active task's Milestone and Release, plus authorization tests for Actor task-detail scope.
- Added prompt tests covering release-aware rules in both primary Planner and PlannerWake prompts.

### Product onboarding
- Confirmed the Release Management feature toggle already exists in project Version Policy settings and is Owner/Admin controlled.
- Expanded the toggle help in English, German, Russian, Japanese and Chinese to explain the settings prerequisite and non-publishing effect.
- Added a localized Roadmap introduction describing Project Settings -> Release -> Milestone association -> task context, immutable Candidate preparation and separate explicit production publication.

### Independent second version scheme
- Added an independent package.json consumer fixture at `ProjectBeacon.Domain.Tests/Fixtures/NpmReleaseProject/package.json`.
- Domain tests load this fixture from test output and verify SemVer `2.4.1`, JSON Pointer `/version`, and the customer-defined `release-{version}` tag pattern.
- This complements the ProjectBeacon four-component numeric-version path and proves the version policy is not coupled to .NET manifests or a `v`-prefixed tag.

### CI #45448270661 — additional compiler finding on older head
- Run [#45448270661](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45448270661) tested head `6d820990b66daf2c7a44c09fb1da8724b3227631`.
- It exposed invalid doubled escape literals in `ReleasePublishExecutor` (`'\\\\r'`, `'\\\\n'`, `'\\\\t'`) and a duplicate `ReadOriginAsync` declaration.
- Fixed the C# character literals, corrected annotated-tag message parsing to use real newline delimiters, and removed the duplicate method. The exact head `803f305f` then had a PR run pending, but that run was cancelled by the subsequent concurrency event before it could verify the fix.
- The new S6 and task-release-context changes are later than that run. Current exact head `331ea5ef` still needs its own successful CI run.

### Remaining
- [ ] Obtain a fresh CI run on the exact current head and repair every failing build/test/model check.
- [ ] Confirm the release-aware context endpoint and package.json fixture compile and pass on Linux/Windows/macOS.
- [ ] Verify tag rulesets and production Environment reviewers through a connection with sufficient permissions; publication remains disabled operationally until verified.
- [ ] Perform final code review of the whole PR and merge only after green required checks and the release/GitLand authority boundary is confirmed.

No release tag or production deployment was created. No SaaS Control Plane or billing changes.



## 2026-11-24 — Configurable Git tag patterns propagated through release CI

**Implementation commits:** `01d8fe15`, `edc1e4ba`, `0f595874`, `3bd77d3f`, `9593d5d8`.

### Fixed incompatibility
- The release workflow no longer assumes that tags start with `v`. It is triggered for Git tags generally (`**`), then the first validation step rejects any tag that is not safe and does not exactly match the tagged commit's `.beacon/release.json` `tagPattern` + version.
- `scripts/verify_release_manifest.py` now validates arbitrary safe tag patterns, including `release-{version}` and hierarchical patterns such as `customer/releases/{version}`. It still verifies the exact tag, manifest, version scheme, source projections, ReleasePrepare commit trailer and allowed changed paths.
- The manifest verifier writes the verified `version` and `tag_name` to `GITHUB_OUTPUT`. The release workflow exposes these as outputs from its tests job.
- Client archives, container build metadata and production version verification now consume the validated version output instead of stripping a leading `v` from the tag.
- Container images are tagged with the immutable commit SHA only. This avoids trying to use a Git tag containing `/` as a Docker image tag; scanning and deployment remain digest-bound.
- Added `scripts/test_verify_release_manifest.py` for the default four-component numeric format, SemVer with a custom prefix, hierarchical tags, exact mismatch rejection, missing/duplicate placeholders and invalid Git refs.
- The same Python tag-policy tests now run in the ordinary Linux PR CI lane as well as the release workflow's test job.

### Safety boundary
- An arbitrary Git tag push starts the release workflow, but the manifest validation step is the first gate. A tag without a valid Beacon release manifest or exact configured tag match fails before any tests/image/client/deployment jobs can proceed.
- No tag was pushed, no GitHub Release was created and no production deployment was invoked.
- GitHub tag protection / ruleset coverage for customer-configured tag patterns remains unverified. Do not test publication until protection and production Environment reviewers are verified.

### Validation
- Manually exercised the safe-tag rules against standard, customer-prefixed, hierarchical and invalid examples.
- The current execution container cannot resolve `github.com`, and no local .NET SDK is installed; the complete Python tests, YAML/workflow validation and .NET checks must be confirmed by GitHub Actions on the latest head.
- Exact-head CI has not yet been rerun after these workflow and validator changes.



## 2026-11-24 — Release workflow no longer assumes a `v` tag prefix

**Implementation head before journal update:** `c47498f77e5464324e824b3fcf6283dcf0d0c5f7`.

### Implementation
- Release workflow now listens for any Git tag. This is not an authorization bypass: `verify_release_manifest.py` is the first test step, and image/client/deploy jobs depend on that gate.
- Manifest validation accepts any configured safe `tagPattern` with exactly one `{version}` placeholder and compares the exact tag name to both the manifest's `tagName` and `GITHUB_REF_NAME`.
- The validator exports `version` and `tag_name` through `GITHUB_OUTPUT`; the tests job exposes them as job outputs.
- Client archive version, container build version and production `/v1/version` comparison use the validated manifest version, not a prefix-stripped tag.
- Container image names now use the immutable commit SHA tag only. Git tag hierarchies are not valid Docker tag syntax and are no longer copied into GHCR image names. Scan and deploy remain digest-bound.
- Added Python unit tests for the default `v0.7.5.9`, a custom SemVer tag `release-2.4.1`, a hierarchical tag `customer/releases/2.4.1`, mismatched tag rejection, duplicate/missing placeholder rejection and unsafe Git ref names.
- These unit tests run in the ordinary Linux PR CI and again in the tag-triggered Release workflow.

### Review / safety
- Unrelated tags may start the workflow, but cannot progress beyond the manifest gate unless the tagged commit contains a valid Beacon ReleasePrepare manifest and exact configured tag/version relationship.
- No real tag has been pushed, no GitHub Release created, and no production deployment run.
- Tag protection for arbitrary project-configured patterns and production Environment reviewers remain unverified. This remains a publication blocker; do not create a test release tag until those protections have been inspected and confirmed.

### Validation state
- Source inspection confirms the release workflow no longer contains `GITHUB_REF_NAME#v` and the client/image/deploy stages consume `needs.tests.outputs.version`.
- Python helper rules were exercised against safe/unsafe tag cases, but the complete Python unit test file and workflow have not yet been run by GitHub Actions on this head.
- The current environment cannot resolve `github.com`; no local checkout or local .NET/Python workflow execution is available. Exact-head GitHub Actions remains the required validation.

## 2026-11-24 — Release workflow UI and task version-impact visibility

**Implementation head before this journal update:** `9b8fa3917649126acc0af9c0e88e8619c37f4877`.

### Product workflow
- Added human-only `POST /v1/projects/{projectId}/releases/{releaseId}/publish`, backed by `PublishReleaseHandler`. Endpoint requires a human principal; handler additionally checks project/organization Owner/Admin and immutable Candidate/publication revisions.
- Roadmap now exposes Prepare Candidate for InProgress releases, Verify Landing after preparation, and a separate explicit Publish confirmation for Candidate. Queued states and preparation/publication failures are shown without offering unsafe duplicate operations.
- Publication confirmation displays release, version, configured tag and target branch. UI never creates a tag itself; it queues the server-authorized PublishRelease command on the workstation pinned by Prepare.
- Preparation PR links are rendered only after checking HTTPS GitHub host/path.
- Candidate cancellation now matches the UI: permitted before publication or after failure-before-tag; blocked while publication is queued or after immutable tag push. Added domain tests for all cases.
- Task Detail displays the configured project's task version impact, Actor rationale and Reviewer decision while Release Management is enabled. It uses the dedicated impact read handler without expanding the shared TaskItemDto contract.
- Added English, German, Russian, Japanese and Chinese strings for the ReleasePrepare/VerifyLanding/Publish flow and Task Detail's impact/review panel.

### Regression coverage
- API test verifies a project-scoped API token with TaskRead + TaskWrite is still Forbidden on the human-only PublishRelease endpoint.
- Domain tests cover Candidate cancellation before publication, while queued, after failure-before-tag, and after tag-pushed failure.
- BUnit test verifies Task Detail renders impact, rationale and Reviewer decision when Release Management is enabled.
- Checked all new localization keys in en/de/ru/ja/zh; no missing keys were found.

### Validation
- These latest UI/API/domain changes have not yet been verified by CI.
- Last inspected PR CI run #45454932005 was on stale head `48f3fce5fc737ce8492cbe3b893025d7f0590599`; Build failed on all three platforms. The run's logs endpoint returned 404, so the cause was not confirmed from logs. Do not treat it as validation of subsequent changes.
- At this checkpoint the feature branch used merge-base `1290eae570d48c79f8523222b3293c5b536de99f`; main has advanced by commit #233 and the branch is one commit behind. No automatic merge/rebase was performed because the commit touches Web files also edited by this feature.
- No release tag has been created and no production deployment has been attempted.

### Next actions
1. Trigger CI on the exact current branch head and repair every compile/test/model/format/localization failure.
2. Reconcile the branch with main commit #233 without overwriting either side's Web changes.
3. Add further Roadmap/API tests if CI identifies gaps.
4. Verify GitHub tag rulesets and production Environment reviewers before enabling real publication.



## 2026-11-25 — Remote GitHub landing reconciliation and CI compiler fixes

**Branch head after fixes:** `1af82bbef762bf5225b2f68ecc1927f9abb379bc`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), open, ready for review, unmerged.

### Remote landing reconciliation added

- Added immutable `RemoteLandingProof` evidence bound to project, task, pinned workstation, latest Review run, GitHub repository/PR, exact task source SHA, merge commit, target branch, fetched remote base SHA, and merge/verification timestamps.
- Extended the AutoSync target endpoint to include remote-landing candidates only when the task is pinned to the authenticated device, is Approved/InProgress, has no Ready/Active/WaitingForUser sessions, has the latest successful landing Review proof for the same device/source/base, and the latest verdict is Approve.
- Added read-only workstation verification: inspect the exact task worktree and clean reviewed HEAD, inspect the single configured GitHub origin, read the matching merged PR, fetch the configured target branch, prove the merge commit is an ancestor of the fetched remote base, re-read the PR to prevent stale-snapshot races, and recheck the local task worktree.
- The verifier never pushes, marks a PR ready, merges, or updates the target branch. It only reports a verified external GitHub merge to the server.
- Added device-only reconciliation API. The server independently revalidates device token/capability, project/runtime attachment, project AutoSync policy, persistent task workstation pin, latest Review proof and approval, and absence of active sessions before atomically storing the proof and closing the task.
- Added EF configuration, migration, model snapshot, DI registration, endpoint and tests. Updated `docs/ignore-query-filters.allowlist` with the exact reviewed bypass counts for the two new handlers.

### CI run #45555763535 — compiler findings

Run [#45555763535](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45555763535) tested head `2b0e7fb33c54e5f8e988cc3c232289896a608f17` and failed on Linux, Windows and macOS Build steps.

Exact compiler findings:
1. `DaemonAutoSyncLoop.cs(164)`: invalid multi-character C# literals used for CR/LF clipping.
2. `GitLandExecutor.cs(114)`: invalid multi-character C# literals used for CR/LF splitting.
3. `FinishWorkHandlerTests.RivalCommittingDb` and `CommentSaveFailingDb`: test doubles did not implement the newly added `IBeaconDb.RemoteLandingProofs` member.

All three causes have been corrected:
- Replaced the double-escaped literals with valid `'\\r'` and `'\\n'` C# char literals.
- Added the missing DbSet forwarding member to both test doubles.
- Scanned every C# file in the 21-file diff since the earlier compile checkpoint for the same doubled CR/LF char-literal pattern; no other occurrences were found.

The current head `1af82bbef762bf5225b2f68ecc1927f9abb379bc` has **not yet been validated by CI**. The historical run remains failed and must not be represented as a green check. Local .NET build is not available in this environment.

### Next actions

1. Trigger CI on exact head `1af82bbef762bf5225b2f68ecc1927f9abb379bc`.
2. Fix any remaining compile/test/model/format failures and repeat until green on the exact head.
3. Reconcile any new main commits without overwriting changes; PR base is main and must be rechecked.
4. Keep publication blocked until tag protection and production Environment reviewers can be verified with sufficient GitHub permissions. No tag or deployment has been created.
5. Perform final review of the RemoteLandingProof path and confirm GitLand and external GitHub landing remain non-competing flows.



## 2026-11-25 — CI follow-up for remote landing reconciliation

**CI run:** [#45557017168](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45557017168)  
**Tested head:** `bb69f36084d83d86d22a5f4a8c441698b8b433f8`  
**Follow-up fix head:** `c2cc391b18f4fbf9a4db89320b89ff9e8729bdee`.

### CI findings

- The previous four compiler findings (invalid CR/LF char literals and missing `IBeaconDb.RemoteLandingProofs` forwarding) were fixed and no longer appear in this run's Linux compiler output.
- Linux reported one remaining compiler error: `DeviceRemoteLandingReconciliationHandlerTests.cs(71,26)` could not resolve `AuditActorKind`.
- Added `using Domain.Entities.Audit;` to that test file.
- CI reported xUnit analyzer warning `xUnit2017` in `NativeAgentToolPolicyTests.cs`; replaced `Assert.True(collection.Contains(...))` with `Assert.Contains(...)`.
- These changes are committed through head `c2cc391b18f4fbf9a4db89320b89ff9e8729bdee`. No CI run has yet validated that head.
- The older run #45557017168 remains failed on the head it tested. Do not count its result as validation of the fixes.

### Landing authority — current state

- GitHub remote landing reconciliation is read-only: it proves an externally merged PR and records the immutable proof; it does not merge.
- `GitLandExecutor` refuses Execute/Revert on GitHub origins, preventing local GitLand and GitHub from becoming competing merge authorities.
- This avoids dual authorities but does **not yet meet the product goal of autonomous agent-led merge**: a GitHub PR still requires an external GitHub merge. This remains an explicit blocker. The next implementation must add a server-authorized merge request/execute operation with deterministic required-check/review/base/HEAD validation and must respect GitHub branch protection (no bypass).
- Non-GitHub/local GitLand behavior remains separate.

### Next validation step

- Trigger CI on `c2cc391b18f4fbf9a4db89320b89ff9e8729bdee`.
- Inspect all platform build outputs, EF pending-model gate, test modules, localization, and format checks. S0–S6 remain unaccepted until exact-head CI is green.

## Update format

For every subsequent work session append:
- date and branch head before/after;
- files and behaviors changed;
- exact checks/tests run and results (including CI run identifiers/URLs);
- completed acceptance criteria;
- remaining work, risks and blockers;
- the next concrete action.

Do not mark an item complete because code was merely drafted or written. A completed item requires verified behavior and recorded checks.


## 2026-11-25 — CI formatting correction

**Branch:** `feat/release-management`  
**Head before:** `079ffaf7aad9427a1a26f4962e4b1fa41dbc4efa`  
**Source-fix commit:** `f5cd1bd51a528e23685f21aa81be83be0f0ff360`

### CI findings on exact prior head

- CI run [#45560509847](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45560509847) completed with failure on Windows, Linux and macOS.
- All three platform jobs completed Restore, Build, EF pending-model check, and all seven test modules successfully.
- The only failure reported by all three jobs was `dotnet format ProjectBeacon.sln --verify-no-changes`: lines 481–499 of `ReleasePublishExecutor.cs` had one extra indentation level (4 spaces).
- Windows and macOS test lanes explicitly skipped the three PostgreSQL persistence tests because `BEACON_TEST_SKIP_POSTGRES=1`; these are reported skips, not passing database integration tests. The separate Linux lane uses its PostgreSQL service and enforces the result count and allowed-skip set through the TRX guard.
- Corrected only the 19 formatter-reported lines in `ReleasePublishExecutor.cs`; no behavior was changed.

### Verification state

- Source correction is committed; no claim of green CI is made.
- The repository CI workflow intentionally does not run for PR `synchronize`; a new explicit workflow-dispatch or PR-ready event is required.
- Next: trigger a fresh CI run on the exact post-fix head, inspect all platform outcomes, and handle any newly exposed failures.

### Remaining blockers

- Landing authority / autonomous GitHub merge is still unresolved: reconciliation records a completed remote merge but does not execute one.
- GitHub tag protections and Production Environment required reviewers remain unverified. No production tag or deployment is permitted.
- Local .NET SDK and ProjectBeacon MCP/Context7 are unavailable in this execution environment; validation must be based on the GitHub Actions runs, with their actual skips and limitations recorded.


## 2026-11-25 — Align version-source validation with the release CI gate

**Branch:** `feat/release-management`  
**Head before this slice:** `216bf097ccbd7ee54cb211ecad92edc7aec2dfaa`  
**Head after source and test changes:** `0fb8f2246433bb68cf8e2b0eae80411b3527aa09`

### Findings and changes

The policy API accepted source selectors that the production release workflow could not evaluate consistently. This could allow a Version Policy save and ReleasePrepare to succeed, only for the tag-triggered release workflow to reject the same source at publication time.

Changed:

- `ProjectBeacon.Domain/Entities/Projects/ProjectVersionPolicy.cs`: XML selectors are now validated as the same portable absolute/descendant location-path subset used by the release gate. Examples: `/Project/PropertyGroup/Version` and `//Version`. Predicates, attribute selectors, axes, functions, wildcards and namespace prefixes fail at policy validation rather than at tag publication.
- The Calendar scheme now accepts the portable format subset shared with Python: one `yyyy`, `MM` and `dd`, optional one `HH`, `mm`, `ss`, and literal separators `.`, `-`, `/`, `_`, space, `:`, `T`, `Z`. Unsupported .NET-only date tokens are rejected when saving the policy.
- `scripts/verify_release_manifest.py`: XML reader now evaluates both absolute and descendant location paths in document order. Regex reader translates common .NET named capture groups `(?<name>...)`, named backreferences `\\k<name>`, and the absolute-end anchor `\\z` to their Python equivalents.
- Added Domain tests for supported/unsupported XML and Calendar selectors, and Python tests for XML selection plus .NET regex group/backreference translation.

### Verification

- A focused Python smoke check of the XML location-path and .NET named-group translation logic returned the expected four-component version for absolute paths, descendant paths, named groups and named backreferences.
- The repository CI run [#45580698958](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45580698958) is still running on the earlier head `216bf097ccbd7ee54cb211ecad92edc7aec2dfaa`; it does **not** include this slice.
- No local .NET SDK is available here. The current source/tests are not marked accepted until a fresh CI run validates exact head `0fb8f2246433bb68cf8e2b0eae80411b3527aa09`.

### Remaining compatibility work

- TOML parsing and the broader .NET/Python regular-expression dialect still need a dedicated parity audit. The changes above fix the concrete named-group mismatch but do not claim full equivalence of both engines.
- GitHub-authoritative remote merge execution remains a blocker: current reconciliation records an externally completed merge but does not execute one.
- GitHub tag rulesets and required reviewers for the `production` Environment remain unverified; no production tag/deployment is permitted.

### Next

1. Trigger and inspect CI on the exact current head.
2. Fix any failures before changing the branch head again.
3. Continue with a server-authorized GitHub landing workflow that respects branch protection, then verify release publication protection and end-to-end product acceptance.


## 2026-11-25 — Cross-platform CI passed; release-gate and settings hardening

**Validated commit:** `9878489da6c71a8f337c7cab8989524666298b68`  
**CI:** [#45582326452](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45582326452), run 1636  
**Result:** all three platform jobs completed successfully.

### Exact CI result

- Linux `build-test-linux`: success — restore, build, EF model check, release-manifest Python tests, API/Application/CLI/Domain/Infrastructure/Web/Worker test modules, TRX guards and `dotnet format`.
- Windows `build-test (windows-latest)`: success — build, EF model check, all seven .NET test modules and `dotnet format`.
- macOS `build-test (macos-latest)`: success — build, EF model check, all seven .NET test modules and `dotnet format`.
- The run was triggered on the exact `9878489d` head. The next follow-up commits below are not covered by this CI run and need their own CI.
- Expected negative-case tests in the Python release-gate suite were printing GitHub `::error::` workflow annotations despite passing. This is a log-noise defect, not a test failure; it is corrected below.

### Follow-up hardening committed after the validated head

- `scripts/verify_release_manifest.py`: Python regex evaluation now runs in an isolated child interpreter with a 2-second hard timeout. This prevents catastrophic backtracking from hanging a release workflow indefinitely. The subprocess uses explicit UTF-8 input/output and does not invoke a shell.
- The release validator retains workflow error annotations when executed as the production script, but imported unit tests now write expected validation failures as ordinary stderr text; they no longer produce misleading GitHub error annotations.
- Added Python tests for timeout fail-closed behavior and for the absence of workflow-command annotations during expected unit-test failures.
- Project settings now document the actual supported XML location-path subset and portable Calendar token/separator set. All five UI locales (en/ru/de/ja/zh) explain that a version source is required before Candidate preparation but may be configured later when the project only plans Releases.
- Calendar format field now displays its dedicated localized help text.
- These follow-up changes are committed on `feat/release-management`; current branch HEAD after the updates recorded here is `65f6eae80afd9b947b63ac6476dfdd7958ee1810`.

### Publication protection inspection

- GitHub `GET /repos/vitalyKl/ProjectBeacon/rulesets` and `?includes_parents=true` both returned an empty array. There are no repository or inherited repository rulesets visible to this integration.
- Classic branch protection returned HTTP 403 (integration lacks access); tag-protection endpoints and the Environment listing are not exposed by this connector. Therefore classic `v*` tag protection and required reviewers for `production` remain unknown, not confirmed absent or present.
- No production tag was created and no deployment was started.

### Remaining blockers and next steps

1. Run a fresh CI on the latest branch head `65f6eae8`; previous success on `9878489d` is not evidence for the follow-up commits.
2. Continue the server-authorized GitHub landing design. Current remote reconciliation is read-only and requires an external merge; don't create a competing local GitLand authority or give Actor a raw merge bypass.
3. Have an administrator verify classic tag protection for `v*`, branch protection, and required reviewers for GitHub Environment `production` in GitHub Settings before any production publication test.
4. Continue end-to-end acceptance of project settings, RLS/authorization, localization and release workflow failure states on an independent user project.


## 2026-11-26 — CI #1640 corrections and GitHub-authoritative landing review

**Branch:** `feat/release-management`  
**CI run:** [#45626583688](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45626583688)  
**Failed head:** `d05a71b963256f1b30af639b5f668acba8f07645`

### CI findings

The same two deterministic test failures reproduced on Linux, Windows and macOS:

1. `IgnoreQueryFiltersAllowlistTests.ProductionTenantFilterBypasses_MatchReviewedPathAndCountAllowlist`: expected 8 calls for `DeviceRemoteLandingReconciliationHandler.cs`, actual source contains 9. Updated the reviewed count to 9. No query behavior was changed.
2. `GitLand_ExecuteAndRevertOnGitHubOriginRequirePullRequestLanding`: the Execute test passed no preview target-head/candidate-tree evidence. The executor correctly rejected the malformed Execute before reaching its intended GitHub remote-base check. The test now forwards the exact `targetHeadSha` and `candidateTreeSha` returned by Preview, so it tests the intended `GitHubReviewBaseUnavailable` fail-closed path.

### GitHub landing implementation review

The current branch now contains `GitHubLandingMergeExecutor` and dispatches to it only for a server-issued `GitLand Execute` on a GitHub origin. Ordinary Actor tools still expose only PR status/ready; they do not expose merge. The executor:

- derives repository/branch/PR from the pinned task worktree and rejects arbitrary origins, multiple push/fetch URLs, mirror mode and review-base mismatch;
- requires clean task worktree and exact reviewed source SHA;
- requires one matching open PR targeting the pinned base; rejects duplicates and a changed base;
- reads the PR snapshot and required checks, repeats snapshot/task/base reads before mutation, and checks non-draft/open, mergeable, clean merge state, APPROVED review, exact PR head and passing non-empty required checks;
- invokes GitHub's PR merge API with the exact expected head SHA and merge method `merge`; no admin/bypass flag is used, so GitHub-side protections still apply;
- handles uncertain responses by re-reading PR state, then fetches the remote target and verifies the merge commit is reachable from the refreshed target;
- returns merge evidence only after exact merge identity is confirmed. GitLand then requires the merge commit tree to match the immutable preview tree and runs the post-merge check before recording `landed`.

GitHub merge API is now the only remote merge authority. Local GitLand ref-only Execute remains for non-GitHub repositories; it cannot mutate a GitHub target. GitHub remote Revert remains blocked and must be implemented as a new reviewed forward-fix task, not a protected-ref rewrite.

### Corrections committed after failed run

- `docs/ignore-query-filters.allowlist`: revised the single reviewed entry from 8 to 9 to match the nine constrained call sites.
- `ProjectBeacon.Cli.Tests/GitLandExecutorTests.cs`: Execute test now carries exact Preview evidence into the authorized Execute payload.

### Current verification state

- Both reported causes were corrected. New exact-head CI is required; CI #1640 remains failed and is not retroactively green.
- No production tag/deploy was run.
- Repository rulesets query returned no rulesets; legacy tag protection and GitHub Environment `production` reviewers are still not verifiable with the current integration.
- GitHub landing integration tests currently cover safe argument construction/response parsing and fail-closed GitLand routing. A successful live merge test against a protected disposable GitHub repository is still not available in CI and remains an acceptance gap.

### Next

1. Run full CI on the updated head and fix any remaining failures.
2. Expand merge executor test coverage for stale base, changed PR head, missing approval, missing/failing required checks, duplicate PRs, API rejection, lost response/already-merged recovery, and successful response identity mismatch.
3. Add an integration test using a disposable protected GitHub repository or a deterministic HTTP/API seam; do not rely on a live production repository.
4. Confirm GitHub rules/reviewers in repository Settings before any release tag or deployment.


## 2026-11-26 — Decouple verified GitHub landing reconciliation from project AutoSync

**Source head after this slice:** d68881c656d3de3f44bb41ea98974a56123750ff  
**Previous CI:** [#45712282443](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45712282443), run 1641, validates earlier head bdd46d1a7a359edada5e06fd67a580ddb7b63a14; it does not include this slice.

### Finding

Project Git policy defaults AutoSyncEnabled=false. Before this fix, both target discovery and the remote reconciliation API treated that preference as a global remote-landing kill switch. As a result, a server-authorized Beacon GitHub landing could pass its own GitLand Execute and persist LandingAttemptStatus.Landed, but the workstation's background loop would never discover the task to record its RemoteLandingProof and close it.

AutoSync is a repository synchronization preference (fetch/fast-forward and tags); it must not disable completion of a landing already initiated and verified by Beacon.

### Changes

- DeviceAutoSyncTargetsHandler now returns device-bound project roots when either:
  - project AutoSync is enabled, or
  - AutoSync is disabled but the pinned task has a latest successful Review/Approve and an exact LandingAttemptStatus.Landed matching the Review run, device, source SHA and target branch.
- A project with AutoSync disabled and no verified Beacon LandingAttempt is still omitted. External/manual PR merges therefore do not silently become enabled when AutoSync is off.
- DeviceAutoSyncTargetDto carries an explicit AutoSyncEnabled flag. The daemon skips GitSyncService.ExecuteAsync and tag sync when false, but always runs the separate remote-landing verifier for any returned tasks.
- ReconcileDeviceRemoteLandingHandler now requires a valid project Git policy. With AutoSync disabled, it accepts only an exact Beacon-managed attempt already in verified Landed state. Without an attempt, AutoSync must be enabled. Existing device binding, task pin, latest Review/Approve, remote PR identity, merge ancestry, active-session and idempotency checks remain in force.
- Updated security allowlist rationale: DeviceAutoSyncTargetsHandler.cs has eight reviewed query-filter bypasses; DeviceRemoteLandingReconciliationHandler.cs has nine.
- Added ReturnsBeaconManagedVerifiedLandingWhenAutoSyncIsDisabled to target discovery tests. Updated ReconcilesWhenBeaconLandingAttemptIsLandedForTheExactMerge to run with AutoSync disabled. The existing negative test still verifies that an external landing without a Beacon attempt is rejected when AutoSync is off.

### Safety boundaries

- AutoSync disabled still means no background fetch/fast-forward and no tag sync.
- Only the workstation pinned to the task can report the merge.
- A Beacon attempt in ExecutePending, LandedUnverified, Failed or any state other than exact Landed is not discoverable for completion.
- The API independently revalidates the exact merge SHA against the immutable LandingAttempt and the latest approved Review.
- External PR reconciliation with no LandingAttempt remains available only for projects that explicitly enable AutoSync.
- No production tag or deployment was created.

### Verification

- New discovery and reconciliation tests are committed. The project IgnoreQueryFilters allowlist is updated to the exact source count.
- CI #1641 is running on prior head bdd46d1a; at the last poll it had no failures, Linux was at formatting verification, and Windows/macOS were still running Application tests. It does not validate this AutoSync-independent landing change.
- A new full cross-platform CI run is required on current head d68881c6.

### Next

1. Run full CI on exact current HEAD and fix failures before accepting this slice.
2. Verify daemon loop tests also cover AutoSync-off with remote-landing-only targets, including no GitSync execution and successful POST reconciliation.
3. Complete deterministic GitHub API seam/integration coverage for merge preflight and lost-response recovery.
4. Keep production publication blocked until an administrator confirms classic v* tag protection and required reviewers for the production environment.


### Daemon-loop coverage added after the landing/AutoSync split

**Implementation/test head before this journal update:** `33ceaa7e5989f52d66883d4feecdf125c33df073`

- Added a narrow injectable remote-landing verifier to `DaemonAutoSyncLoop`; production default remains `AgentTaskGitHubRemoteLandingOperations.VerifyAsync`.
- Added `DaemonAutoSyncLoopTests.ReconcilesVerifiedBeaconLandingWhenAutoSyncIsDisabledWithoutRunningGitSync`. It supplies an AutoSync-off target, fake verifier and fake HTTP API, and asserts one target GET, one reconciliation POST, exact merge evidence in the POST body, and no AutoSync cycle failure. The loop receives `GitSyncService = null` in this test, so an accidental normal GitSync call fails the test instead of being silently counted as a skip.
- Full verification is still pending on the current slice. Previous CI #1641 runs on `bdd46d1a` and therefore does not include the AutoSync-independent code or this daemon-loop test. At the last poll Linux had completed successfully; Windows and macOS were in CLI tests.
- The branch still does not perform a production tag, deploy or merge PR #230.


### Additional negative discovery case — external Approved task is not eligible with AutoSync off

**Source/test head before this journal update:** 7b0c811ebdc3040c0de7dfcddb0f8d8028efaf94

The AutoSync-off target-discovery test now creates two tasks on the same pinned workstation and project:
- one has the exact latest Review proof and Beacon LandingAttempt=Landed; it must be returned for remote-landing reconciliation;
- the other has its own successful Review and Approve verdict but no LandingAttempt; it must not be returned.

The test asserts exactly one remote landing target, preventing ordinary externally managed Approved work from being swept into reconciliation when AutoSync is disabled.

The new daemon-loop test is in ProjectBeacon.Cli.Tests/DaemonAutoSyncLoopTests.cs. It injects the remote-landing verifier and a fake HTTP handler, passes no GitSyncService, and asserts a single reconcile POST without any AutoSync cycle failure. This covers the full background branch where AutoSyncEnabled=false.

CI run #1641 is still tied to the older bdd46d1a head; run #1642 was queued for 15ac31a9 before this last regression assertion. Neither validates source/test head 7b0c811e. Trigger a fresh full matrix on the final journal-updated HEAD.


### GitHub merge queue / stacked PR limitation identified

- The current GitHubLandingMergeExecutor calls the synchronous REST endpoint PUT /repos/{owner}/{repo}/pulls/{number}/merge with exact head SHA and merge_method=merge.
- Current GitHub documentation states that this synchronous endpoint does not support merge queues or stacked pull requests and recommends the asynchronous merge API. The async API can use merge_action=default to select a configured merge queue, but an "enqueued" result is not a completed merge; the PR must be checked later for actual MERGED state. Reference: https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request-asynchronously.
- Therefore direct GitHub PR merge is implemented, but repositories configured to require merge queue or stacked-PR landing are not yet supported. Do not describe GitHub landing as fully compatible with every branch-protection mode.
- Implementing async/queue support needs durable merge-request identity/recovery, an explicit queued/pending UI state, and polling/reconciliation that records RemoteLandingProof only after the PR actually reaches MERGED and its merge commit/tree/post-check are verified. An enqueued result must never mark LandingAttempt as Landed.


### Backward-compatible capability negotiation for AutoSync-off remote landing

**Implementation/test head before this journal update:** eb3de7a5121e52fbc01aba134c3f493d0cbb5082

A compatibility review found that a workstation on the older git-auto-sync-v1 protocol could deserialize the extended target DTO while ignoring AutoSyncEnabled=false, then enter the legacy unconditional GitSync path. That could unexpectedly fetch/fast-forward when the project had AutoSync disabled.

Changes:

- Added WorkstationCapabilities.RemoteLandingReconcileV1 with wire value git-remote-landing-reconcile-v1.
- Current WorkstationProbe advertises it only when the daemon has a supported Git task-branch workflow (same prerequisites as GitLand).
- DeviceAutoSyncTargetsHandler returns an AutoSync-off, remote-landing-only target only when the pinned device advertises RemoteLandingReconcileV1. Legacy AutoSyncV1-only devices receive no such target.
- ReconcileDeviceRemoteLandingHandler independently rejects AutoSync-off reconciliation from a device without the new capability; the prior exact LandingAttempt= Landed / Review/source/device/target/merge checks still apply.
- Updated the positive AutoSync-off discovery test to first query as a legacy daemon and assert an empty result, then advertise the new capability and assert that only the Beacon-managed Landed task is returned. The external Approved task without a LandingAttempt remains excluded.
- Updated reconciliation fixtures to advertise the new capability and added a Domain capability unit test for legacy vs new protocol support.
- This is a capability gate, not a change to the AutoSync preference: AutoSync=false still performs no normal branch fetch/fast-forward and no tag synchronization.

Verification context:
- CI #45714185084 (run 1643) is tied to earlier source/test head e0fb1f07. Linux completed successfully across build, model check, all test modules, TRX and format. At the last poll Windows and macOS had reached CLI tests with no reported failure. This run does not include the new capability code/tests.
- A new full matrix must run on the latest journal-updated head.


### Backward-compatible remote landing capability gate and API defense

**Source/test head before this journal update:** acaa5b097dedbcd21bd148a1745e7acf5d063178

A follow-up compatibility review showed that an older daemon advertising only git-auto-sync-v1 could ignore the new AutoSyncEnabled=false DTO field and treat a remote-landing-only target as a normal AutoSync target. The fix uses explicit protocol negotiation:

- Added WorkstationCapabilities.RemoteLandingReconcileV1 (git-remote-landing-reconcile-v1).
- WorkstationProbe advertises it only if task-branch Git workflow is supported and GitHub CLI (gh) is installed; the verifier needs gh to inspect the PR and prove its merge.
- AutoSync-off target discovery returns a target only if the pinned device advertises RemoteLandingReconcileV1. Older AutoSyncV1-only workstations receive no remote-landing-only target.
- ReconcileDeviceRemoteLandingHandler independently rejects AutoSync-off reconciliation without RemoteLandingReconcileV1, in addition to requiring an exact verified Landed attempt. The legacy AutoSyncV1 check remains for the loop transport itself.
- The target-discovery test now first uses a legacy probe (git-sync-v1 + git-auto-sync-v1 + git-land-v1) and asserts no target; after upgrading the probe with the new capability, it asserts exactly the Beacon-managed Landed task is returned and the external Approved task without a LandingAttempt remains excluded.
- Added an API-level legacy-daemon test: even with a fully verified Landed attempt, a device lacking the new capability receives Forbidden and the task/proof remain unchanged.
- Added Domain capability negotiation tests; capability is advertised only with supported task branch workflow and gh present.

### Verification state

- CI #45714185084 (run 1643) is on the earlier source/test head e0fb1f07 and does not include capability negotiation. Linux completed all 24 steps successfully. Windows had reached CLI tests and macOS Web tests at the latest poll, with no reported failures.
- The new capability commits and tests are not yet validated by CI. A fresh complete Linux/Windows/macOS matrix must run on the current journal-updated head.
- No production tag/deploy or PR merge was performed.


## 2026-11-26 — GitHub async merge queue landing

**Implementation branch:** `feat/release-management`  
**Open PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)  
**Source/test head for this slice:** `64268979e60ea6f932673c21b87dc37c6fad3ba8`

### Implemented

- Replaced the synchronous GitHub PR merge endpoint with the asynchronous `PUT /pulls/{number}/merge-async` flow. The request pins the exact reviewed head SHA and uses `merge_action=default`; it never requests a rules bypass.
- Added explicit `pending` / `enqueued` handling. An accepted request is recorded as a durable queued state on the existing `LandingAttempt`; it stays `ExecutePending` and cannot close the task.
- Handles repeated requests where GitHub returns HTTP 409 and the prior async request body, including recovery of the existing request UUID. An already-enqueued response without a UUID is also persisted; the PR state itself remains the polling authority.
- Lost Execute acknowledgement is recoverable by read-only remote PR reconciliation. This is still not an Actor merge tool: the mutating call remains inside server-authorized GitLand Execute, and AutoSync only observes GitHub state.
- AutoSync discovers queued Beacon landing attempts independently from repository AutoSync enablement. Legacy workstations do not receive an AutoSync-off reconciliation target without the explicit `git-remote-landing-reconcile-v1` capability.
- The pinned workstation waits for the PR to become `MERGED`, fetches the configured target branch, verifies the merge commit is reachable, and checks the merge commit tree against the immutable previewed candidate tree.
- The workstation re-runs the exact `ReviewCheckCommand` in a detached temporary worktree at the merge commit. It verifies HEAD/tree/clean state and sends a SHA-256 output proof. The temporary worktree is removed after the check.
- Server reconciliation requires the exact LandingAttempt ID, ReviewRun, source SHA, target branch, device pin, async merge UUID when one exists, candidate tree, successful post-merge check and output hash. It then advances the attempt to `Landed` and closes the task in the same database save.
- Both workstation target discovery and server reconciliation compare the current project check command with the hash pinned by the ReviewRun and LandingAttempt. A changed command leaves the task open; the new command is not substituted silently.
- Tightened the executor documentation to make the timing explicit: GitHub evaluates branch/repository rules when the queued or direct merge is actually executed, not when the asynchronous request is first accepted.

### Tests added or extended

- Domain tests for queued attempts with and without a request UUID, and recovery after an uncertain Execute acknowledgement.
- CLI tests for async API arguments, pending/enqueued/merged/failed response parsing, the 409 response body, and the no-UUID enqueued case.
- GitLand completion test proving an enqueued/no-UUID completion remains `ExecutePending` and records a queued audit event.
- AutoSync target-discovery test covers AutoSync disabled, queued merge discovery without UUID, the exact candidate tree and pinned check command, plus command-drift behavior.
- Application reconciliation tests cover queued merge proof, wrong request UUID, wrong LandingAttempt ID, changed ReviewCheckCommand, and the transition to Landed/task Closed only after all exact evidence is supplied.

### Verification state

- CI #45715872864 passed all Linux/Windows/macOS jobs on the earlier async-landing foundation head `22fc248c7a616db0805d18b97273572c4e9db72a`. It predates this slice and does **not** validate the current changes.
- CI #45586302515 also passed on the earlier `3eed928` head; it is not evidence for the async queue implementation.
- Current source/test head `64268979e60ea6f932673c21b87dc37c6fad3ba8` has not yet had a fresh full CI run. A new Linux/Windows/macOS matrix is mandatory; no green status is claimed.
- Local .NET SDK and local ProjectBeacon test harness are unavailable in this execution environment.

### Remaining blockers

1. Run and inspect the full CI matrix on the exact post-documentation implementation head; fix any compiler, analyzer, test or format failures.
2. GitHub repository rulesets API returned an empty list. Classic tag/branch protection and GitHub Environment `production` reviewers remain inaccessible to this integration (403/unsupported endpoint); an administrator must verify them before a production tag/deploy.
3. Validate end-to-end on a user project configured with a merge queue, including the case where an async merge request UUID is absent/expired and the PR itself is the only durable polling source.
4. GitHub-authoritative merge still has to respect configured rules. No direct merge, force-push, bypass, production tag, deployment, or PR merge was performed by this work.

### Next

1. Trigger CI on the current branch head after the journal/plan commits.
2. Review all three platform jobs and any diagnostics from the new async merge tests.
3. If green, continue the remaining product acceptance for release settings, localization, RLS, UI error states and independent project version schemes.


## 2026-11-26 — Complete GitHub async-merge failure reporting

**Implementation head before this journal entry:** `c5982175ec5040cdafdb2a05dace1f8e2fe9dd2b`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)  
**Scope:** GitHub-authoritative landing only; no SaaS Control Plane or billing changes.

### Implemented

- The pinned workstation's AutoSync verifier now distinguishes a terminal GitHub async-merge `failed` state from `pending`, `enqueued`, expired/unreadable request records and ordinary PR status changes.
- Before reporting failure, the workstation rereads the PR and requires the same open PR identity, exact task head SHA, review base and canonical GitHub URL. A stale UUID or changed/closed/merged PR is not reported as a terminal failure.
- AutoSync posts the exact device-bound failure proof to `POST /v1/devices/me/remote-landings/failure`. It does not call the successful reconciliation endpoint for a failed request.
- The API route is device-authenticated. The server checks the device/project runtime binding, AutoSync capability (and the explicit AutoSync-independent reconciliation capability when relevant), task/device pin, current Approved state, exact LandingAttempt, ReviewRun, source/base, queued async UUID, unchanged ReviewCheckCommand hash, latest Approve verdict and absence of active pipeline sessions.
- Only after all checks does the server mark the attempt `Failed` with `GitHubAsyncMergeFailed`, set the escalation code and write a device audit event. The task remains InProgress/Approved; no RemoteLandingProof is written and no task is closed.
- Task Pipeline UI now maps `GitHubAsyncMergeFailed` to a localized, actionable landing reason.
- Removed a duplicate API handler and redundant `/fail` alias. The single supported route is `/failure`, matching the AutoSync client.
- Added Application coverage for exact UUID acceptance/rejection and task remaining open. Added an AutoSync loop test proving the failure endpoint is called and successful reconciliation is not called for a terminal failure.

### Verification state

- Full CI run [#45763146032](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) is running on the earlier async-landing head `8ff19877b27e8fd699eaa5047eec7b26965f0e0e`; it does **not** include the definitive-failure reporting slice.
- Exact current implementation head before this log update: `c5982175ec5040cdafdb2a05dace1f8e2fe9dd2b`. No CI run is registered for this exact head yet.
- The AutoSync loop test for failure reporting is present in `ProjectBeacon.Cli.Tests/DaemonAutoSyncLoopTests.cs`; Application tests cover the server transition. The new cross-platform matrix must validate these together.
- Previously green runs #45582326452 and #45586302515 predate the async merge queue implementation and are not evidence for this slice.
- Local .NET SDK/test harness is unavailable in this execution environment; acceptance is based on the exact GitHub Actions head and its full job results.

### Remaining blockers

1. Trigger and inspect Linux, Windows and macOS CI on the final head after this journal update; resolve any compiler/test/analyzer/format failures.
2. Validate a real user project with an enabled GitHub merge queue: normal enqueued→merged path, definitive async failed path, lost/expired UUID with PR polling fallback, and ReviewCheckCommand drift.
3. GitHub rulesets API returned no repository/inherited rulesets. Classic branch/tag protection and Production Environment required reviewers remain unverified due connector permissions. An administrator must confirm these before any production tag/deploy.
4. The implementation has not been merged into `main`; no production tag/deployment has been created.

### Next

1. Run CI on the exact post-journal branch head and fix failures.
2. Finish end-to-end merge-queue acceptance on a separate user project and verify the UI recovery path after `GitHubAsyncMergeFailed`.
3. Reconcile plan/journal against the final PR state, then request review/merge separately. Do not publish a release as part of this implementation slice.


## 2026-11-26 — UI assertion and current-head correction

After the failure-reporting entry above, the branch received UI regression coverage:

- Added `ProjectBeacon.Web.Tests/Bunit/TaskPipelinePanelTests.cs::FailedGitHubMerge_ShowsLocalizedFailureReason`. It constructs an InProgress/Approved task with a Failed landing attempt and verifies that the panel renders the human-readable localized GitHub async merge rejection message.
- Rechecked `DeviceEndpoints.cs`: exactly one `/v1/devices/me/remote-landings/failure` route and one `ReportRemoteLandingFailure` handler remain.
- Rechecked `DaemonAutoSyncLoopTests.cs`: the definitive-failure test verifies one failure POST, zero successful-reconciliation POSTs, exact LandingAttempt/merge request IDs and no cycle exception.
- Rechecked Application reconciliation tests: a wrong async request UUID is rejected without changing the attempt; the exact UUID records Failed and an audit event while keeping the task InProgress/Approved with no RemoteLandingProof.
- Current implementation head before this journal correction: `43747d09cd31bd3adcc1fb9ed7ee94ea5b7b9b4c`. The latest commit adds the BUnit assertion; previous journal text naming `c5982175` is the pre-UI-test head and is superseded by this correction.
- CI run [#45763146032](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) is still an older-head run (head `8ff19877`); there is no CI run for `43747d09`. A full run on the post-journal exact HEAD remains mandatory.


## 2026-11-26 — Definitive GitHub merge-queue failure reporting and UI

**Implementation branch:** feat/release-management  
**Open PR:** https://github.com/vitalyKl/ProjectBeacon/pull/230  
**Implementation head before this journal update:** faf9f8e9ab96f17cc8e220d289d1c2089379c881

### Implemented

- Added Device-authenticated endpoint POST /v1/devices/me/remote-landings/failure, separate from successful-merge reconciliation. It is protected by RequireDevice; no user/Actor GitHub merge tool was introduced.
- The pinned workstation reports failure only after GitHub returns async request state "failed", then re-reads the exact PR and confirms it remains OPEN with the same target branch, reviewed source SHA and canonical PR URL.
- Server-side ReportFailureAsync validates the active device, project runtime attachment, AutoSyncV1 and (when normal AutoSync is disabled) RemoteLandingReconcileV1, pinned task workstation, exact latest LandingAttempt ID, exact ReviewRun, request UUID, source/target identity, approved verdict, unchanged ReviewCheckCommand hash, and absence of an active pipeline session.
- The handler transitions only the latest matching ExecutePending attempt to Failed with GitHubAsyncMergeFailed, writes an audit event and leaves the task InProgress/Approved. It does not close the task, create a remote landing proof, push, merge, or modify refs. Stale attempts and mismatched UUIDs are rejected.
- AutoSync posts the failure report through the Device endpoint and logs a clear instruction to request a fresh Preview. The failure reason is localized on en/ru/de/ja/zh in the task landing UI.
- Added Application test for wrong UUID rejection and exact failure recording; added AutoSync loop test proving the failure endpoint is called and successful reconciliation is not; added BUnit test for the localized failed-merge message.
- Removed the redundant AlreadyRecorded field from the new failure response contract.

### CI findings and exact coverage

- CI #45761895374 (https://github.com/vitalyKl/ProjectBeacon/actions/runs/45761895374) ran on the earlier head 34b19bc7. All seven test modules, TRX guards and builds succeeded on Linux before dotnet format failed; macOS failed at the same formatting step; Windows was cancelled by the matrix's fail-fast behavior.
- Exact format diagnostics were three xUnit2031 findings in DeviceAutoSyncTargetsHandlerTests.cs at lines 272, 277 and 290: Assert.Single(collection.Where(...)) must use Assert.Single(collection, predicate). The three assertions are corrected in a later commit.
- CI #45763146032 (https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) was triggered on head 8ff19877. It includes the first server-side failure-report handler and xUnit assertion fix, but predates the Device endpoint, AutoSync client POST, Application/CLI tests, localized UI reason and BUnit test. It is not coverage for the final branch state.
- Current implementation head before this journal update is faf9f8e9ab96f17cc8e220d289d1c2089379c881. A new full CI on the post-journal head is mandatory; no green result is claimed for the newest changes.
- Local .NET SDK/test runner is unavailable in this execution environment.

### Remaining

1. Verify the outcome of #45763146032 on its exact 8ff19877 head; fix any findings relevant to its scope.
2. Run full Linux/Windows/macOS CI on the final branch head including the Device failure endpoint, AutoSync client path, tests and localized UI.
3. Validate end-to-end on a user project with an actual GitHub merge queue, especially pending, enqueued, failed, no/expired UUID, and ReviewCheckCommand drift.
4. Production tag/branch protections and required reviewers for GitHub Environment production remain unverified through this integration. No production tag/deployment is permitted until an administrator checks GitHub Settings.


## 2026-11-26 — Resolve Application test failures in async-merge reporting

**Source-fix head:** `2260a7c8c99a6f488286a784751e67f97a57966a`  
**CI with failures:** [#45764313765](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45764313765), run 1648, on pre-fix head `eb76ae90bdb8573c8d1c8183e995f5f37bf03c61`.

### Exact failures from Linux TRX

1. `DeviceRemoteLandingReconciliationHandlerTests.RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen` failed because the new audit payload serialized anonymous members `request.Repository` and `request.PullRequestNumber` using PascalCase, and `AuditEvent.ValidateDetails` correctly rejected the non-allowlisted key `Repository`.
   - Fixed by emitting only existing allowlisted audit keys: `deviceId`, `sourceBranch`, `targetBranch`, `sourceSha`, `reviewRunId`, `policyCode`, `resultCode`.
   - The attempt ID remains the audit event TargetId; the failure code is stored in the dedicated attempt fields. No free-form repository/PR/request UUID strings are added to audit details.

2. `IgnoreQueryFiltersAllowlistTests.ProductionTenantFilterBypasses_MatchReviewedPathAndCountAllowlist` expected 9 calls but found 18 in `DeviceRemoteLandingReconciliationHandler.cs`.
   - The first 9 calls belong to successful remote-landing reconciliation; the additional 9 belong to the new authenticated failure-report handler.
   - Reviewed all 18 call sites: every device lookup is constrained by authenticated `deviceId`; project/runtime/task/review/verdict/session/attempt lookups are constrained by the exact request `projectId`, `taskId`, attempt identity and/or device binding. The two flows separately validate the immutable task workstation pin, proof and status before recording anything.
   - Updated `docs/ignore-query-filters.allowlist` to count 18 and explain the success and failure proof boundaries. No production query was removed or weakened.

### CI state and verification

- On run #45764313765, Linux, Windows and macOS all reached the Application module and marked it failed. Linux TRX reported the two failures above; CLI, Domain, Infrastructure, Web and Worker subsequently passed on Linux. TRX guards passed with 2,663 results, 7 modules and the permitted platform skip.
- The fixes are committed on the implementation branch at `2260a7c8c99a6f488286a784751e67f97a57966a`. No CI result exists for this post-fix head yet.
- The previous green runs #45582326452 and #45586302515 predate the async merge queue/failure-report flow and do not validate it.

### Next

1. Trigger a fresh full CI on the exact post-journal head; confirm Application passes and all three platform jobs complete.
2. If Application is green, finish Linux TRX guards, Windows/macOS tests and formatting before accepting the slice.
3. Continue end-to-end merge-queue acceptance, including the safe "no async UUID" state (PR polling can confirm eventual MERGED, but a terminal failure without exact UUID remains unknown and must not be falsely recorded as Failed).


## 2026-11-26 — Windows CLI test timeout correction after GitHub merge-queue integration

**CI failure:** [#45765744398](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45765744398), run 1649, on exact head `a1f277731f66ccadc319f204eede3db3632b6251`.

### Exact failure scope

- Linux and macOS jobs completed successfully.
- Windows build, EF model check, API, Application, Domain, Infrastructure, Web and Worker tests passed; CLI module had three failures:
  1. `NativeCommandRunnerTests.ArgvCommandRunsWithoutShellAndReturnsStructuredResultAndLogId`: `dotnet --version` exceeded the test's hard-coded 15-second timeout. The runner correctly returned `timedOut=true` and `exitCode=null`; the test then attempted to read the null exit code as a number.
  2. `NativeAgentRuntimeTests.NativeRuntimeWaitsForCommandApprovalBeforeRunningArgvAndResumesAfterReply(decision: "once")`: the test stopped waiting after 200 × 20 ms (4 seconds), before its approved `dotnet --version` command completed.
  3. The same resume test with `decision: "always"` hit the same four-second polling limit.
- This was a Windows cold-start/test-budget failure, not a GitHub landing assertion or a compile failure.

### Fixes committed

- `NativeCommandRunner` sets `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, and `DOTNET_NOLOGO=1` in the isolated command environment. No user CLI state or credentials are shared.
- The direct runner test allows 60 seconds and now asserts `timedOut=false` before reading `exitCode`; its diagnostic includes duration and output tail if the command still times out.
- The approval/resume test waits up to 50 seconds for the bounded command and includes the last tool result in its failure message.
- `NativeCommandRunnerTests` now also asserts that all three isolated .NET environment switches are set by `CreateStartInfo`.
- Source and test commits: `90b6990815beb12c44bcf26c84e16784c6b1366e`, `6b3669b2339a3081fd34bebe53b1471d561bdcd9`, `fd953347cc89b6c4e07b9722850b1e6dc97ab8e5`.
- The environment-contract assertion test was added in commit `4457acb742cb5bf2448399a4d4c5bb39eede0e1c`; implementation head before this journal amendment: `4457acb742cb5bf2448399a4d4c5bb39eede0e1c`.

### Verification state

- No CI run covers these three follow-up commits yet. The successful Linux/macOS jobs from #45765744398 do not validate the Windows fix.
- A new full Linux/Windows/macOS matrix must run on the exact post-journal head.
- No production tag/deploy or PR merge was performed.

### Next

1. Trigger and inspect full CI on the current post-journal branch head; verify all three Windows CLI tests pass.
2. If green, continue final product acceptance for GitHub merge queue pending/enqueued/merged/failed paths and the no-UUID uncertainty path.
3. Keep classic branch/tag protection and Production Environment reviewers as an administrative blocker until verified in GitHub Settings.


## 2026-11-26 — Prevent false test failures when a newer PR CI run cancels the previous run

**Incident:** CI #45775401460 (run 1650), triggered for head `43b611406c9370e9dd022b4b61c4cffed2672127`. A second PR run #45775571535 (run 1651) was created for the same SHA. The workflow has `concurrency.cancel-in-progress: true`, so #1650 was superseded while its Build step was running.

### Confirmed failure mechanism

- Linux logs show `The operation was canceled` in Build. Because test steps used `if: always()`, the API `dotnet test --no-build` still ran after cancellation; its test executable had not been produced (`Win32Exception (2): ... ProjectBeacon.API.Tests ... No such file or directory`), so the test host reported `Zero tests ran` and exit code 1. TRX guards then found six reports instead of seven.
- Windows job was cancelled before Build completed; subsequent `dotnet test` invocations reported `No test projects were found` or missing test executables. These are cancellation artifacts, not evidence of failing tests.
- Linux Application (1,017; 1 skip), CLI (668), Domain (315), Infrastructure (119), Web (253), and Worker (24) completed successfully on the canceled run. The API module did not execute any tests. Do not count run 1650 as a test pass or as a product-code regression.
- GitHub queued run #45775571535 (run 1651) for the same source SHA `43b611406c9370e9dd022b4b61c4cffed2672127`; it started after run 1650 cleared. It validates the code changes at that source head but uses the previous workflow-file revision.

### CI/release workflow correction

- In `.github/workflows/ci.yml`, all 16 test, TRX report and TRX guard conditions changed from `if: always()` to `if: ${{ !cancelled() }}`.
- In `.github/workflows/release.yml`, the eight test and TRX-guard conditions changed the same way. The separate scan/SBOM upload remains `if: always()` so diagnostic artifacts still upload on failure/cancellation.
- This preserves the intended behavior: after a normal Build/test failure, run remaining modules to collect complete diagnostics; after the workflow itself is cancelled/superseded, do not launch `--no-build` tests against partial or missing binaries.
- Implementation commits: `14a8b606b99fdb96c678dd4d04873e99565a5f10` (CI), `216901136fbd16da006b04da59910f776227d652` (release workflow).
- Current source/workflow head before this journal append: `216901136fbd16da006b04da59910f776227d652`.

### Verification and next steps

1. Run #45775571535 (run 1651) is the current validation of the earlier source head `43b61140`; do not infer that it validates the new workflow gating.
2. After run 1651 finishes, trigger one fresh full Linux/Windows/macOS CI on the current head after this journal update. Verify the Windows cold-start test fix and cancellation-safe workflow expressions together.
3. Confirm that a normal build/test failure still allows all later test modules and TRX reporting, while a superseded run skips test/report/guard steps.
4. No PR merge, production tag or deployment was performed.


## 2026-11-26 — Cross-platform CI passed after Windows CLI timeout fix

**CI:** [#45775571535](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45775571535), run 1651  
**Exact source head:** `43b611406c9370e9dd022b4b61c4cffed2672127`  
**Result:** Linux, Windows and macOS jobs all completed successfully; no failed steps.

### Platform verification

- Linux: Build and EF model check passed; all seven test modules passed; TRX report and guards passed; 2,663 total test results, 0 failed, 1 allowed platform-conditional skip; `dotnet format --verify-no-changes` passed.
- Windows: Build and EF model check passed; all seven test modules passed, including CLI (668 total, 0 errors, 0 failed, 0 skipped); `dotnet format --verify-no-changes` passed. This directly validates the previous three Windows CLI timeout failures.
- macOS: Build and EF model check passed; all seven test modules passed; `dotnet format --verify-no-changes` passed.
- Windows/macOS test lanes set `BEACON_TEST_SKIP_POSTGRES=1`; PostgreSQL integration coverage remains the Linux lane's responsibility. Platform-conditional skips remain reported, not counted as executed passes.

### Scope and limitation

- This run validates the source/tests at `43b61140`, including the isolated `dotnet` first-run environment and longer, bounded approval/resume test waits.
- It does **not** validate the later workflow-only changes `14a8b606` (`ci.yml`) and `21690113` (`release.yml`), nor the journal update on the new branch head.
- Current branch head before this journal append: `71439aae751c7ccb4804269b79458bcfeb096815`.

### Next

1. Trigger one full CI matrix on the current post-journal head. Confirm the new `!cancelled()` conditions parse and the same tests remain green.
2. Once the current head is green, continue end-to-end acceptance of GitHub merge queue: enqueued→merged, definitive failed with exact UUID, missing/expired UUID, PR identity drift and ReviewCheckCommand drift.
3. Keep classic branch/tag protection and Production Environment required reviewers as administrative blockers until verified in GitHub Settings. No production tag/deploy or PR merge was performed.


## 2026-11-26 — Fix Windows SQLite disposal race in the Web test fixture

**CI:** [#45778443033](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45778443033), run 1652  
**Failing test:** `ProjectBeacon.Web.Tests.Bunit.ChatPollRecoveryTests.ChatDock_PollSurvivesTransientFailure`  
**Failure:** `SqliteConnection.Close()` threw `InvalidOperationException: Collection was modified; enumeration operation may not execute.` during `BUnitRenderBase.Dispose()`.

### Diagnosis

- CI #45778443033 failed only in the Windows Web test module: 249 passed, 3 platform-conditional tests skipped, 1 failed. Linux and macOS full jobs passed; all other Windows test modules passed.
- The failure occurred in the shared bUnit fixture teardown, not in the GitHub merge executor. `BUnitRenderBase.Dispose()` closed the shared in-memory SQLite connection before disposing the bUnit renderer.
- `ChatDock` owns background polling. The renderer must dispose components and scoped services while their shared database connection is still alive; closing SQLite first can race command/statement cleanup during component teardown.

### Fix

- Changed teardown order in `ProjectBeacon.Web.Tests/Bunit/BUnitRenderBase.cs`:
  1. dispose the bUnit renderer (components, timers/poll loops and scoped services);
  2. dispose the fixture's root `BeaconDbContext`;
  3. dispose the shared in-memory SQLite connection.
- The test itself remains unchanged; no retry, longer timeout, or swallowed exception was introduced.
- Source-fix commit: `41cf2f82ea6e3adb3db74793e0262775c10160a5`.

### Verification state

- The fix is committed on `feat/release-management`; current branch head at this journal entry is `41cf2f82ea6e3adb3db74793e0262775c10160a5`.
- CI #45778443033 validates the preceding head `13a157373004b49be000fb735c83f5fff4dbb7ef`, not this teardown change.
- A fresh full Linux/Windows/macOS matrix must run on the current head. Windows must pass `ChatDock_PollSurvivesTransientFailure` and the full Web module; formatting must also pass.

### Next

1. Trigger and inspect exact-head CI on `41cf2f8`.
2. If green, continue GitHub async merge acceptance: `pending → merged`, `enqueued → merged`, definitive `failed` with exact UUID, expired/missing UUID, and mismatched PR/review identity.
3. Keep tag/branch protection and Production Environment reviewer checks as administrative blockers. No production tag, deployment, or PR merge was performed.


## 2026-11-26 — Full cross-platform CI green after SQLite teardown fix

**Validated HEAD:** `a37e6f5a8115650b579cc756eddc148db76ad4f8`  
**CI:** [#45783053165](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45783053165), run 1653  
**Result:** Linux, Windows and macOS jobs all completed successfully.

### Exact result

- All three platforms: Restore, Build and EF no-pending-model-changes check passed.
- All seven test modules passed on Linux, Windows and macOS: API, Application, CLI, Domain, Infrastructure, Web and Worker.
- Linux authoritative Postgres lane also passed release-manifest tests, TRX result-count/allowed-skip guards and `dotnet format`.
- Windows Web passed, including the previously failing `ChatDock_PollSurvivesTransientFailure`; the teardown no longer closes the shared SQLite connection before bUnit disposes components and scoped DbContexts.
- Windows and macOS format checks passed.
- No test failures, formatter failures or cancelled jobs remain in this run.

### Scope

- This run validates the SQLite disposal-order fix from `41cf2f82ea6e3adb3db74793e0262775c10160a5` plus the preceding source at exact head `a37e6f5a8115650b579cc756eddc148db76ad4f8`.
- It does **not** validate changes made after that head. The next AutoSync proof-enrichment fix will require a new full CI run.

### Next

1. Ensure an already-`Landed` immediate GitHub merge exports its persisted candidate-tree and post-merge proof to AutoSync. The current target handler includes only the LandingAttemptId for this state; the daemon's verifier then refuses to reconcile because required proof fields are absent.
2. Reconcile an already-`Landed` attempt using its immutable stored post-merge exit code/hash (do not rerun a potentially changed project command); queued async merges still need the pinned ReviewCheck to run on the final merged tree.
3. Add regression coverage for immediate merge and ReviewCheckCommand drift, then run full CI on the new exact head.
4. Continue live merge-queue acceptance; no production tag, deployment or PR merge was performed.

## 2026-11-26 — Preserve immutable post-merge proof during AutoSync reconciliation

**Baseline CI:** [#45783053165](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45783053165), run 1653, green on `a37e6f5a8115650b579cc756eddc148db76ad4f8`.
**Source/test head for this follow-up:** `1c9cfdd43477c46375cecf9a809a386f06d9a48a`.

### Finding

A direct GitHub async merge can return `merged` immediately. GitLand then verifies the exact candidate tree and pinned ReviewCheck and records `LandingAttempt.Status = Landed`, together with the candidate tree SHA, post-merge exit code and output hash.

The AutoSync target builder previously exported only `LandingAttemptId` for this state. The daemon's remote verifier requires a candidate tree and post-merge evidence to reconcile the exact remote merge, so this left already-verified immediate merges stuck in `Approved`. Re-running the project setting's current ReviewCheckCommand was also incorrect: a later settings change must not retroactively invalidate a completed, immutable landing proof.

### Changes

- `DeviceAutoSyncTargetsHandler` now includes the persisted candidate tree, post-merge exit code and output hash for an already-`Landed` attempt.
- `AgentTaskGitHubRemoteLandingOperations` still verifies that GitHub's merge commit tree exactly matches the candidate. For an already-`Landed` attempt it reuses the immutable stored check proof and does not rerun the mutable project setting.
- An async request still pending/enqueued has no post-merge result yet; its AutoSync target continues to include only the ReviewCheckCommand whose hash matches the immutable Review proof. The pinned workstation executes that check on the exact eventual GitHub merge commit.
- `ReconcileDeviceRemoteLandingHandler` now compares the incoming candidate tree, post-merge exit code and output hash against the persisted `Landed` attempt, in addition to checking the exact merge commit.
- Application tests assert that a later ReviewCheckCommand edit does not remove or invalidate persisted proof for a completed land, while queued merges receive no fabricated post-merge proof.
- Reconciliation tests now reject missing post-merge proof and a mismatched candidate tree; they accept the exact persisted proof even after the project setting has changed.

### Verification

- The preceding exact-head CI #45783053165 passed all platforms and all seven test modules plus formatting; Windows Web regression passed.
- The source and tests in this entry were added after that green run and are **not covered** by it.
- No local .NET SDK is available here. A new full Linux/Windows/macOS CI run on the current branch head is required before this slice is accepted.

### Next

1. Run the exact-head CI and fix any build, test or formatting failures.
2. Keep live merge-queue acceptance open: pending → merged, enqueued → merged, definitive failed with exact request UUID, expired request, missing UUID, PR/base/head drift and ReviewCheck drift.
3. Verify GitHub `v*` tag protection and required reviewers on the Production Environment before any production publication. No production tag, deployment or PR merge was performed.