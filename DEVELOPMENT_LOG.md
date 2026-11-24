# ProjectBeacon — Release Management Development Log

**Implementation branch:** `feat/release-management`  
**Plan branch:** `docs/release-management-plan`  
**Initial base:** `main` at `22a1a9e559a97124f939b6da2de2e4f9612f4d94`  
**Current PR base (24 Nov 2026):** `main` at `1290eae570d48c79f8523222b3293c5b536de99f` (`0.7.5.9`)  
**Started:** 2026-11-24  
**Scope:** universal ProjectBeacon product feature for customer projects; no SaaS Control Plane, billing, or platform-operator work.

This log is maintained in the repository and updated in the same branch as implementation. It is the execution record; the plan remains the requirements baseline.

## Status

- Overall: **In progress**
- Current slice: **S3 — ReleasePrepare and immutable Candidate evidence**
- S0 AutoSync implementation is present; **not accepted until green CI on the current head**
- S1 Version Policy / task impact implementation is present; **not yet fully CI verified**
- S2 Release entity, Milestone association, feature gate, API and Roadmap UI are implemented; **not yet fully CI verified**
- S3 ReleasePrepare / VerifyLanding client-server path and immutable Candidate snapshot are implemented; **not yet fully CI verified**

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

1. Trigger CI on the exact current branch head and fix compile, migration, domain, authorization, UI, localization and platform failures.
2. Complete S3 verification: inspect immutable candidate snapshot, remote target SHA and ReleasePrepare/VerifyLanding reconciliation paths.
3. Resolve the GitHub-versus-GitLand authority boundary before release publication; do not add a second merge executor.
4. Complete gated tag creation and GitHub Actions workflow observation only after tag protection and production reviewers are verified.
5. Continue S4–S6 on this branch, keeping this log current after every verified checkpoint.

## Blockers and decisions

- **Landing authority:** current PR #194 contract and plan recommendation differ. No code change may make GitHub and local GitLand competing authorities. Keep existing GitLand semantics until a single remote-authoritative transaction is designed and tested.
- **GitHub protection:** rulesets and production environment reviewers not yet read.
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


## Update format

For every subsequent work session append:
- date and branch head before/after;
- files and behaviors changed;
- exact checks/tests run and results (including CI run identifiers/URLs);
- completed acceptance criteria;
- remaining work, risks and blockers;
- the next concrete action.

Do not mark an item complete because code was merely drafted or written. A completed item requires verified behavior and recorded checks.
