# ProjectBeacon — Release Management Development Log

**Implementation branch:** `feat/release-management`  
**Plan branch:** `docs/release-management-plan`  
**Initial base:** `main` at `22a1a9e559a97124f939b6da2de2e4f9612f4d94`  
**Current PR base (27 Nov 2026):** `main` at `ed4dace38357f5062478ba3322148ab352d916ce` (`0.7.5.10`)  
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
| Landing authority | PR #194's original contract remains the local GitLand executor, but this feature branch adds a server-authorized GitHub async merge path for validated remote PRs. | GitHub is authoritative for a remote merge; the pinned workstation executes the request and AutoSync reconciles only after MERGED proof. No bypass of repository rules or competing local merge authority. |
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

1. Record the exact-head CI result and run one final matrix after the journal/plan corrections below.
2. Keep the first production publish blocked until the repository owner verifies release-tag protections and Environment production reviewers, deployment refs and secrets in GitHub Settings.
3. Once those external safeguards are confirmed, complete the first-publish acceptance review using a dedicated release candidate; do not create a production tag from this implementation PR.
4. Keep PR #230 open until code checks and the external production safeguards are both accepted.

## Blockers and decisions

- **Landing authority — resolved in this branch:** the server-authorized GitLand Execute path now calls GitHub's asynchronous merge API with the exact reviewed PR head SHA and the repository's default merge action. Before submission it revalidates the task worktree, pinned review base, PR identity, APPROVED decision and required checks. `pending`/`enqueued` is not treated as a landing; AutoSync observes the exact request until GitHub reports MERGED and then verifies merge commit/tree and post-merge evidence. No `bypass_rules`, admin merge or forced ref update is requested. The earlier wording that remote PRs still need an external manual merge is obsolete.
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


## 2026-11-26 — Support GitHub merge-queue merge-group trees

**Implementation head:** `9fce7b94c699ac8313bc9f149347035c32404ddd`  
**Prior full green CI:** [#45786178874](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45786178874) on `bd4378bb09d883df6d64f80542e198df775b5aef`; this run does not cover the source changes below.

### Finding

The GitHub async merge endpoint can enqueue a PR for a merge queue. The queue validates and merges a temporary merge-group commit built from the then-current base and earlier queued PRs. Its tree can legitimately differ from Beacon's local Preview candidate even though the exact reviewed PR head is what GitHub merged. Comparing the final queue merge tree byte-for-byte with the Preview tree would therefore strand a valid queued merge as `LandedUnverified`.

### Changes

- Added `GitHubLandingMergeExecutor.IsPostMergeTreeAcceptable`: a valid actual merge tree is required in all cases; an expected candidate tree is additionally required only when a workflow expects the merge to reproduce a local candidate exactly.
- GitHub-origin `GitLand Execute` no longer rejects a valid final merge solely because merge-queue processing included a newer base or earlier queued PRs. The async merge request still pins the exact source SHA, and the merge executor still validates repository, PR identity/base, clean task worktree, review approval, required checks and mergeability.
- The pinned post-merge check now runs on the actual GitHub merge commit/tree. AutoSync verifies that commit is present in the fetched remote target branch, re-confirms the exact merged PR and checks the immutable result before requesting server reconciliation.
- Normal local GitLand still passes the Preview candidate tree as the expected tree and remains strict. This relaxation applies only to the GitHub-authoritative merge path.
- Added regression cases for strict candidate equality, queue tree divergence, malformed actual tree and malformed expected candidate SHA.

### Verification

- The changes are committed to `feat/release-management`; latest source/test head is `9fce7b94c699ac8313bc9f149347035c32404ddd`.
- CI #45786178874 passed all three OS jobs on the previous head `bd4378bb`; it does **not** validate this merge-queue correction.
- A fresh Linux/Windows/macOS CI run is required on the exact current head. No .NET tests or formatting are claimed passed for this follow-up until that run completes.

### Remaining acceptance items

1. Run and inspect exact-head CI; fix any compile, test or formatter issue.
2. Exercise both direct merge and merge-queue paths against a disposable GitHub repository with the required `merge_group` workflow checks configured. This environment has not performed a live queue merge.
3. Verify legacy `v*` tag protection and required reviewers for GitHub Environment `production` in repository settings. No production tag/deploy is permitted until confirmed.
4. Keep PR #230 open for review; do not merge this development PR as part of the release workflow.


## 2026-11-26 — Restrict tree divergence handling to confirmed merge-queue requests

The initial queue compatibility change was deliberately narrowed after rechecking the GitHub async response states.

### Final rule

- **Immediate async response `status=merged`:** GitLand still requires the actual merge commit tree to equal the Preview candidate exactly. If the base changed in the preflight-to-merge window or GitHub produced a different tree without an acknowledged queue state, the attempt becomes `LandedUnverified` with `GitHubMergeTreeMismatch`; it is not silently accepted.
- **Async response `pending` or `enqueued`:** the attempt stays `ExecutePending` and records the queued request state. AutoSync later confirms the exact PR/head is `MERGED`, confirms the merge commit is reachable from the fetched remote target, validates the actual merge-group tree SHA, and executes the pinned ReviewCheck on that actual commit. It does not require the merge-group tree to equal the local Preview candidate because GitHub may have incorporated a newer base or earlier queued PRs.
- **AutoSync distinction:** a persisted successful post-merge proof identifies an immediate `Landed` attempt and keeps exact candidate-tree equality; a queued attempt has no persisted post-merge proof yet and uses the queue-only relaxed tree validation before running the check.
- The server still compares request candidate tree, PR/source/review identity, merge request UUID where present, merge commit and post-merge exit/hash against the immutable LandingAttempt. Only then can it record RemoteLandingProof and close the task.
- Normal local GitLand never uses this relaxation and remains exact-tree-only.

### Changes after the first queue compatibility commit

- Updated the GitHub-origin immediate merge path back to strict `request.TargetTreeSha` equality.
- Updated AutoSync to choose strict tree validation when a persisted immediate post-merge proof exists, and queue-only validation when it is still waiting for its post-merge check.
- Corrected comments in target discovery and the shared tree validator to reflect this distinction.

### Verification status

- The first fresh CI run [#45789363602](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45789363602) was dispatched on head `c0c1b7b9`, before this final narrowing. It is not validation for the current source.
- Latest source/documentation-on-implementation branch head at this entry: `05ce597c5d1e2fd6dcbec2ca195e802407efc9fd`.
- A new full Linux/Windows/macOS CI on that exact final head is required. No live GitHub merge-queue exercise has occurred; use a disposable repository with merge_group checks configured.

### Next

1. Finish or inspect the older run #45789363602 without treating it as final acceptance.
2. Trigger a fresh exact-head CI after all source, tests and journal entries are committed.
3. If green, test immediate merge tree strictness and queued merge-group divergence in a disposable GitHub repository.


## 2026-11-26 — Do not redispatch an already queued GitHub merge

### Finding

After GitHub returned `pending` or `enqueued`, a later manual Execute request could reach `ExecuteLandingHandler` after the original workstation command had completed. Without a queue-state guard, it could overwrite `GitHubMergeQueued` with a new dispatch lease and enqueue a second GitLand Execute while GitHub was already processing the same PR.

### Changes

- Added `LandingAttempt.IsGitHubMergeQueued`, which is true only for the explicit `GitHubMergeQueued` or `GitHubMergeQueued:{uuid}` states.
- `ExecuteLandingHandler` now returns the existing attempt without dispatching another command when GitHub has already acknowledged the PR as pending/enqueued. AutoSync remains the polling/reconciliation owner until the exact PR becomes `MERGED` or the async request fails.
- A generic `CommandCompletionUnknown` is deliberately not classified as a confirmed queued merge: it can also be produced by local GitLand, so an explicit retry must remain possible. AutoSync may still perform its read-only remote check for the uncertain case.
- Added Domain tests proving queue markers (with and without an async UUID) are distinct from generic unknown completion, and an Application test proving repeated Execute does not insert a second GitLand command after a queue acknowledgement.

### Verification

- Source/test head after the guard and tests: `77b73cf187022c39b072e4ca50a2fa0434931fa4`.
- CI #45789363602 (run 1655) was started on `c0c1b7b9` and is not validation for the current branch head.
- The final queue-only tree narrowing, target comment, queue retry guard and new tests all require a fresh full Linux/Windows/macOS CI on the exact current head.
- No live merge-queue test, production tag/deployment, or PR merge has been performed.


## 2026-11-26 — Close queued landing attempts when the exact GitHub PR is closed unmerged

**Source/test head:** `561fbd82b28b94cfda5afdd9723490ce70bf68cb`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)

### Finding

AutoSync reconciled an exact merged PR and reported definitive asynchronous merge failures while a PR remained open. However, if a user or maintainer closed the queued PR without merging it, GitHub's PR list contained neither an `OPEN` nor a `MERGED` match. The verifier returned no action, leaving the Beacon `LandingAttempt` in `ExecutePending` indefinitely.

### Changes

- AutoSync now detects a `CLOSED` PR for the exact task branch and target base only when the server-provided landing target proves that GitHub had explicitly acknowledged the async/queue request (`GitHubMergeQueued` marker or merge-request UUID).
- The pinned workstation re-reads the exact PR and verifies its state is `CLOSED`, not merged (both `mergedAt` and `mergeCommitSha` must be absent), and that its URL, base branch and head SHA still match the immutable task/review identity.
- A closed-unmerged PR is reported using the existing device-authenticated failure endpoint with the explicit code `GitHubPullRequestClosedWithoutMerge`. Existing open-PR async failures and queue-entry removals continue to use `GitHubAsyncMergeFailed`.
- The server accepts only the two whitelisted code/state pairs: `GitHubAsyncMergeFailed + OPEN` and `GitHubPullRequestClosedWithoutMerge + CLOSED`. The report is still bound to the latest queued `LandingAttempt`, its pinned workstation, exact `ReviewRun`, task source SHA/base and approved verdict.
- The failure transition keeps the task `InProgress/Approved`, writes audit evidence, creates no `RemoteLandingProof`, and leaves the task eligible for a fresh Preview after the failure has been reviewed.
- Added CLI tests for the state-to-failure mapping and rejection of `MERGED`/unknown states; added an Application/SQLite test for the exact closed-unmerged report, state mismatch rejection, retained task state, no merge proof and audit actor.
- Centralized the supported failure-code classification and made the final named cancellation-token arguments explicit.

### Verification

- Source/test commits: `53c7291574a69b1b2bf31c4e474bdb96ee782605`, `3fe2441d6a5d8fb13f9fdc66f1b95e86404a8d9a`, `9049e428698f2943dfee866b192aaf0356a90243`, `7dca44aad95a4777d6c0099521b61c71957e23e4`, `561fbd82b28b94cfda5afdd9723490ce70bf68cb`.
- The tests are committed, but no local .NET SDK is available here. A new exact-head Linux/Windows/macOS CI is required; no green status is claimed for this slice yet.
- No live GitHub merge-queue exercise was performed. Validate closed-unmerged and queue-removal cases in a disposable repository before full acceptance.

### Remaining blockers and next

1. Run the full CI on the exact implementation head after this journal commit, and fix any compile/test/format failures.
2. Continue disposable-repository merge-queue acceptance: pending/enqueued, queued to merged, queue entry removal, PR manually closed without merge, PR/base/head drift, lost/expired request ID and failed post-merge check.
3. Classic tag protection `v*` and required reviewers for GitHub Environment `production` remain unverified; no production tag or deployment is permitted.
4. Keep PR #230 open for review. Do not merge the development PR as part of release publication.


## 2026-11-26 — Correct audit allowlist compatibility for queued PR failure reports

**Failed CI:** [#45794974665](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45794974665), run 1656, exact head `0c56ffbc234ebf57a984b8a7ce80c53440d93eb1`  
**Follow-up source fix:** `fb00cf6d37f9b93d0319054361014cd69db8177b`

### CI finding

- Linux completed the full job and reported three Application test failures:
  - `RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen`
  - `RecordsQueueRemovalWithoutAsyncRequestIdOnlyForExplicitQueuedAttempt`
  - `RecordsClosedUnmergedPullRequestAsQueueFailureForTheExactQueuedAttempt`
- macOS reported the same Application failure stage (API passed; Application failed; CLI continued successfully). Windows was still running CLI when the log was inspected; the source-level cause was already identical and deterministic.
- All three failures had the same message: `Audit detail key 'pullRequestState' is not allowlisted.` The failure transition itself reached the audit event creation and was rejected by the existing audit schema guard.

### Correction

- Removed the unallowlisted `pullRequestState` field from the audit details in `ReportFailureAsync`.
- Retained the allowlisted `policyCode` and `resultCode` fields. `GitHubPullRequestClosedWithoutMerge` remains distinguishable from `GitHubAsyncMergeFailed` through those existing fields; the request state is validated before audit and does not need to be copied into arbitrary audit JSON.
- No weakening of audit allowlisting or change to the failure state transition was made.

### Verification and next

- The red run #45794974665 is for the previous `0c56ffbc` head and does not include the audit fix.
- Source fix is committed in `fb00cf6d37f9b93d0319054361014cd69db8177b`; run a fresh full Linux/Windows/macOS CI on the exact head after this journal update.
- If the next Application test run passes, verify remaining CLI/Domain/Infrastructure/Web/Worker modules and formatting across the matrix.
- Disposable live merge-queue acceptance is still outstanding. Production tag protection `v*` and required reviewers for Environment `production` remain unverified; no production tag/deploy is permitted.


## 2026-11-26 — Preserve GitHub async pending/enqueued state across the 24-hour request window

**Branch:** `feat/release-management`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)  
**Last fully green baseline before this slice:** [CI #45796862947](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45796862947), exact head `e2cc19ed38937fe3cc86d38f10f23b9cc6f7fb28`.

### Finding

The GitHub async merge request UUID is retained in `LandingAttempt.FailureCode`, but the prior marker did not preserve whether GitHub's response was `pending` or `enqueued`. GitHub keeps async request records for a limited 24-hour period. If a request was only `pending`, its UUID could later become unreadable while the PR remained open and had no merge-queue entry. Treating that missing entry as queue removal would incorrectly fail a still-running request.

### Changes

- `LandingAttempt` now stores distinct markers:
  - `GitHubMergePending:{uuid}` for an accepted `pending` request;
  - `GitHubMergeEnqueued:{uuid}` for an `enqueued` request;
  - `GitHubMergeEnqueued` when GitHub confirms queue insertion without a UUID.
- Added `GitHubMergeRequestStatus`, `IsGitHubMergePending`, and `IsGitHubMergeAccepted`. Any acknowledged async request prevents a second Execute dispatch; only explicit `enqueued` state is treated as proof of queue entry.
- `GitLandCompletion` persists the workstation's `mergeRequestStatus` together with its request UUID. A `pending` response without UUID is rejected by the domain transition.
- AutoSync target discovery now sends `MergeRequestStatus` to the pinned workstation.
- AutoSync distinguishes two separate facts:
  - an accepted pending/enqueued async request (enough to recognize the Beacon-managed PR, including closed-unmerged verification);
  - a confirmed merge-queue entry (required before an absent queue entry can be reported as queue removal).
- When the persisted state is `pending` and its UUID can no longer be read, a missing/expired async request response is not treated as queue removal. The verifier reports the unresolved request and keeps polling. If a later query returns `enqueued`, normal merge-queue polling begins.
- Legacy `GitHubMergeQueued` no-UUID state is preserved as enqueued. Legacy UUID markers remain accepted for exact PR polling, but unknown subtype does not become evidence of queue removal when its UUID expires.
- No database migration was needed: status remains in the existing bounded `FailureCode` string and the workstation target DTO uses an optional backward-compatible field.

### Regression tests

- Domain tests cover pending UUID, enqueued UUID, enqueued without UUID, rejection of pending without UUID, and legacy marker compatibility.
- Application tests verify that the persisted pending status and UUID reach AutoSync target discovery.
- GitLand completion tests verify that the exact result marker and pending UUID are written to the LandingAttempt.
- Application landing recovery tests verify that a repeated Execute does not enqueue a second command for an acknowledged pending merge.
- CLI tests distinguish accepted async requests from confirmed queue entries, including the expiry case: persisted `pending` + unreadable UUID + missing queue status remains non-terminal.

### Verification state

- The prior baseline CI #45796862947 passed Linux, Windows and macOS on `e2cc19ed38937fe3cc86d38f10f23b9cc6f7fb28`; it does not include the current changes.
- Source/test changes are committed, but this exact new head has not yet been validated by CI. Run the full Linux/Windows/macOS matrix and fix any compile/test/format failures before treating this slice as accepted.
- No live disposable-repository merge-queue test has been performed. It remains necessary to verify pending >24h expiry, pending→enqueued, enqueued UUID expiry with a live queue entry, queue removal, closed-unmerged PR and eventual merge.

### Remaining blockers

1. Full exact-head CI on the current branch.
2. Live disposable GitHub merge-queue acceptance; unit tests are not a substitute.
3. Classic tag protection `v*` and required reviewers for Environment `production` remain unverified through the current integration. No production tag/deployment is permitted.
4. PR #230 remains open and unmerged; do not merge the development PR as part of release publication.


## 2026-11-26 — Correct two regressions found by async-merge state CI

**Failed CI:** [#45841796175](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45841796175), run 1658, exact head `c877b4039712bff62e75f233f7f5fd21a21bb0c4`.  
**Follow-up source/test commits:** `78b8edd8a2cf889ad15b7388e8de7aec495750d7`, `605e0cfddd7f5af9241f0ffba35de06e27178e6f`.

### Exact findings

1. **Application test project did not compile:** `PipelineHandlerTests.LandingRecovery.cs(135,46)` read `GitHubMergeRequestStatus` from `LandingAttemptDto`, although the property belongs to the `LandingAttempt` entity. This produced `CS1061` on Linux. The assertion was unnecessary for the DTO contract: the test already checks the persisted failure marker on the entity after reload. Removed only the invalid DTO assertion; no API/DTO field was added.
2. **Four Domain tests had invalid fixtures:** their `RecordPreview` calls passed a 40-character string in the `candidateCheckOutputHash` position, while that field must be a 64-character SHA-256. Updated the four new test fixtures to use 64 characters. No production validation was weakened.

### CI interpretation

- Linux Build failed with the single `CS1061` above. Application test step then could not start its executable because the test assembly was not produced; this was a consequence of the compile error, not an independent application behavior failure.
- Linux Domain module ran and reported exactly four failures, all the invalid candidate-check-output-hash fixtures listed above.
- Linux API, CLI (701 tests), Infrastructure, Web and Worker modules passed.
- Windows/macOS were still running when the compiler error and Domain failures were diagnosed; do not count the old run as a successful cross-platform validation.
- The fixes are committed, but the exact new head has not yet been validated. A new full Linux/Windows/macOS run is required.

### Next

1. Let run #45841796175 finish and record its final platform outcomes; it remains a historical failing run.
2. Trigger a new full CI on the latest head after this journal update.
3. Confirm Application and Domain pass on exact current head, then verify remaining CLI/Infrastructure/Web/Worker and formatter steps.
4. Keep merge queue live acceptance and production tag/reviewer protections as separate outstanding blockers.


### CI #45841796175 — final cross-platform outcome

The previous run is now fully complete. Linux, Windows and macOS all finished with the same source-level failures:

- All three Build steps failed on the single compiler error `CS1061` at `PipelineHandlerTests.LandingRecovery.cs(135,46)`: the test attempted to read `GitHubMergeRequestStatus` from `LandingAttemptDto`. The assertion has been removed in commit `78b8edd8a2cf889ad15b7388e8de7aec495750d7`; the persisted entity-marker assertion remains.
- All three Application test modules failed because the test assembly was not produced after the Build error; this is not an independent runtime regression.
- The Linux Domain module and Windows/macOS Domain modules all ran and each reported the same four test failures. The TRX stack traces showed `ArgumentException: Candidate check output hash is invalid` from four new test fixtures passing a 40-character string. Fixed to 64-character SHA-256 fixtures in `605e0cfddd7f5af9241f0ffba35de06e27178e6f`.
- Linux API, CLI (701 tests), Infrastructure, Web and Worker passed. Windows/macOS API and CLI passed; their later module/format outcomes did not override the compile/domain failures.
- The old run remains red by design; its results cannot be reused for the fixed head.

**Current branch head after these corrections and journal updates:** `bbb039d6f17853470a713f1dddf4162ebf28ff32`. Trigger a fresh full CI for this exact head. No green claim is made until all Linux/Windows/macOS jobs and formatting pass.


## 2026-11-26 — Fix stale async merge state assertion

**Failed CI:** [#45844150177](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45844150177), run 1659.  
**Validated branch head:** `77b9cccc185254ec35ee0f28aaf4dc0e9891f32b`.  
**Test-only correction:** `e7f848ffd604c0ecbe9da6371e07e5299bf7d76c`.

### Exact CI finding

- Linux, Windows and macOS all failed one and only one Application test:
  `DeviceRemoteLandingReconciliationHandlerTests.RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen`.
- The test called `LandingAttempt.MarkGitHubMergeQueued(requestId)`. The current API deliberately maps that compatibility overload to async merge state `enqueued`, and the entity persists `GitHubMergeEnqueued:{requestId}`.
- The assertion still expected the pre-split legacy marker `GitHubMergeQueued:{requestId}`. The implementation and the rest of the async merge protocol were correct; this was a stale test expectation.
- Updated only that assertion to the explicit `GitHubMergeEnqueued:{requestId}`. No production code, protocol, or legacy compatibility behavior was changed.
- All other Application tests passed on all three OS jobs. Build, API, CLI, Domain, Infrastructure, Web and Worker passed; format passed on the jobs. Linux PostgreSQL integration coverage was active (not skipped); Windows/macOS use the documented PostgreSQL skips.

### Verification

- The source/test correction is committed as `e7f848ffd604c0ecbe9da6371e07e5299bf7d76c`.
- A fresh full cross-platform CI run is required for this exact head; run #45844150177 remains red and does not validate the correction.
- Existing async merge safeguards remain: exact PR/head/base identity, exact async request UUID when pending, explicit distinction between `pending` and `enqueued`, no duplicate Execute after accepted async request, fail-closed handling of unknown/expired request state, and post-merge proof before task closure.

### Next

1. Trigger and inspect full CI on the post-fix head.
2. Continue the outstanding disposable-repository GitHub merge-queue acceptance (pending expiry, pending→enqueued, queue removal, closed-unmerged PR, merge completion, and PR/head/base drift).
3. Keep production tag/deploy blocked until an administrator verifies the `v*` tag rules and `production` Environment reviewers.


## 2026-11-26 — Persist GitHub async merge-queue state across AutoSync cycles

**Implementation head before this journal entry:** `4e042dece7fa251c2b273fb55326af58fbe30bf5`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)

### Defect and intended state transition

A GitHub asynchronous merge request can begin as `pending` and later become `enqueued`. Beacon persisted the initial state from GitLand completion but previously observed the later `enqueued` state only in one AutoSync cycle. If the workstation missed that cycle, the persisted state remained `pending`. GitHub retains an async request record for a limited period; after expiry the UUID endpoint can return not-found even while a merge-queue entry remains live. Beacon could then remain stuck in pending and could not safely distinguish queue removal from request-record expiry.

### Implementation

- Added `LandingAttempt.RecordGitHubMergeEnqueued(requestId)`: only the exact persisted request UUID can transition from pending to enqueued; repeats are idempotent; mismatched UUIDs, unknown state and attempts outside ExecutePending fail closed. The task remains ExecutePending and no merge SHA or landing proof is created.
- Added device-only endpoint `POST /v1/devices/me/remote-landings/status` and `ReportAsyncMergeStatusAsync`. It accepts only an explicit `enqueued` observation for an OPEN PR, validates canonical repository/PR identity, exact task/attempt/request UUID/source/base, pinned device/runtime, latest successful Review proof, latest Approve verdict and absence of an active pipeline session. It records an audit event once, never closes the task, and is idempotent.
- The pinned workstation now verifies the exact open PR/head/base twice before reporting `pending → enqueued`. AutoSync posts the status report separately from merge reconciliation; successful status persistence is not treated as a merge.
- Added expiry recovery: when the short-lived async request record is no longer readable, a live GraphQL merge-queue entry for the same open PR can recover the missing enqueued observation. A missing queue entry while only pending/expired is **not** treated as queue removal. Only an explicitly accepted/enqueued request may produce a definitive queue-removal failure.
- Updated failure reconciliation to accept a definitive failure for an explicitly accepted async request in either pending or enqueued state, provided the exact request UUID matches. Generic `CommandCompletionUnknown` without an accepted async request remains ineligible.
- Updated TaskPipelinePanel reason mapping and translations (en/ru/de/ja/zh): pending and enqueued are distinct informational states, not red failure alerts. The request UUID is never shown to the user.

### Tests added/updated

- Domain: monotonic/idempotent pending-to-enqueued transition, mismatched UUID rejection and no transition from an unrecognized status.
- Application: exact device/task/Review/attempt/request binding; rejects closed PR and state regression; idempotent status report and one audit record; verifies AutoSync targets subsequently return the persisted enqueued state; task remains InProgress/Approved and no RemoteLandingProof is created.
- Application: explicit async failure is accepted while the exact request remains pending.
- API: authorization inventory explicitly classifies the new status endpoint as Device-only.
- CLI: AutoSync posts the status report without reconciling or closing the task; expiry recovery requires a real queue entry.
- Web: BUnit coverage for localized pending and enqueued messages and UUID redaction.

### CI status and validation

- Previous run [#45850686745](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45850686745) ran on older exact head `9505ab11`, before this implementation slice. All three OS jobs completed test modules but failed `Verify formatting`. The Linux log reported xUnit1026 for an unused `requestId` parameter in `HasConfirmedMergeQueueEntry_RequiresEnqueuedEvidence`; removed that unused parameter and redundant theory data in commit `b5082889d4bb1ceb2b5d71b197b8d6bab9c3e1c0`. This older run does not validate that change or the current slice.
- Current head before the journal update is `4e042dece7fa251c2b273fb55326af58fbe30bf5`. No CI run has yet validated the new endpoint, the client/AutoSync path, pending failure acceptance, UI localization or new tests. Trigger a fresh full Linux/Windows/macOS run on the post-journal head.
- The .NET SDK is unavailable in this execution environment; no local build/test result is claimed.

### Remaining blockers

1. Run and fix cross-platform CI on the exact current head.
2. Complete live merge-queue acceptance in a disposable GitHub repository, covering pending expiry, recovery by live GraphQL entry, queue removal, closed-unmerged PR, merge completion and PR/head/base drift.
3. Classic `v*` tag protection and required reviewers for GitHub Environment `production` remain unverified through the connector; no production tag/deploy is permitted until an administrator verifies them.
4. Continue user-project acceptance of Release/landing settings, RLS/authorization, localization and failure/recovery UI. SaaS Control Plane and billing remain outside this work.

### Next

1. Trigger exact-head cross-platform CI.
2. Inspect the new status round-trip and test outcomes; fix any failures before further feature changes.
3. Finish disposable-repository queue acceptance and administrative release-protection verification.


## 2026-11-26 — Fix cross-platform build and test regressions in GitHub landing

**Working branch:** `feat/release-management`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)  
**Pre-fix head tested by CI:** `f1692d47e60afa071cd7bc60cf25d834f4ddc0dd`  
**Current source/test head before this journal entry:** `b218893f54fea16792ec005af399e795df1a3a6b`

### Findings from CI #45859153428

The run was triggered on the exact old head `f1692d47`. Linux and macOS completed; Windows was still running its CLI test module at the last inspection. The old run is not evidence for the current source/test head.

1. **Cross-platform build failure — xUnit delegate signature.** Linux and macOS logs reported `CS0407` in `ProjectBeacon.Domain.Tests/LandingAttemptTests.cs:167`: a `bool`-returning `RecordGitHubMergeEnqueuedWithoutRequestId()` method group was passed to `Assert.Throws`, which expects an `Action`. Changed the assertion to a statement lambda that discards the return value while still asserting the expected exception. Commit: `a67f3aaef8235357f52b9ebe31fb39f326a6bf8c`.

2. **Application test failure — audit metadata allowlist.** `task.remote_landing.merge_enqueued` persisted repository/PR/request identity, but `AuditEvent.AllowedDetailKeys` rejected the `repository` key. Added a narrow allowlist for `repository`, `pullRequestNumber`, `pullRequestUrl`, and `mergeRequestId`; repository and PR URL values now have dedicated strict patterns (canonical `owner/repository` and HTTPS `github.com/{owner}/{repository}/pull/{number}`). Added positive and negative Domain tests. Commits: `856b67bb4f2843951eab33d51f46c68c2a9e7f61`, `c65007a9a9af21eee9561353eac662eaff41e30f`.

3. **Application test failure — tenant-filter bypass manifest count.** Recounted every `IgnoreQueryFilters()` call in all 40 allowlisted source files. The `DeviceRemoteLandingReconciliationHandler.cs` entry was stale at 18; the current source has 27 calls. Updated the reviewed count and rationale to 27. All 40 manifest counts were checked against the corresponding current source file. Commit: `c38eab3709b4857e07574cf07b4a84347747b8fb`.

4. **Web test failure — MudBlazor CSS implementation detail.** `PendingGitHubMerge_ShowsProcessingMessageWithoutLeakingRequestId` expected the literal CSS class `mud-alert-info`, but the current MudBlazor rendering does not emit that class. The pending text and UUID redaction were not the failing assertions. Replaced the brittle CSS assertion with a semantic component assertion: locate `MudAlert` and assert `Severity.Info`. Commit: `b218893f54fea16792ec005af399e795df1a3a6b`.

### Exact validation state

- CI [#45582326452](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45582326452) was fully green on earlier head `9878489da6c71a8f337c7cab8989524666298b68`; it predates the async GitHub merge-queue implementation and must not be carried forward.
- CI [#45859153428](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45859153428) tested `f1692d47`. macOS and Linux completed with Build/Application/Domain/Web failures described above; Linux TRX guards also failed because of those tests. The Windows job had completed Build with the same failure and Application tests failed, while CLI tests were still running at the last poll.
- The new audit, allowlist, Domain assertion and BUnit fixes are after `f1692d47` and have not yet been tested by GitHub Actions.
- Local .NET SDK remains unavailable in this execution environment; no local build/test result is claimed.
- New CI must be triggered on the exact post-journal branch head. The previous CI result is not a pass for the current branch.

### Remaining blockers

1. Run fresh Linux/Windows/macOS CI on the exact current head and fix any newly exposed errors.
2. Complete async GitHub merge-queue live acceptance in a disposable repository: pending expiry, recovery from a live GraphQL queue entry, queue removal, closed-unmerged PR, merge completion, and PR/head/base drift.
3. Classic `v*` tag protection and required reviewers for GitHub Environment `production` remain unverified through the connector. Do not create production tags or deploy.
4. Continue user-project acceptance for Release/landing UI, RLS/authorization, localized states and failure/recovery behavior. SaaS Control Plane and billing remain out of scope.


## 2026-11-26 — Correct bUnit 1.40 DOM assertion

**Source commit:** `99d90d9798a43c86458b686852731b36d19c49ed`

The exact CI run [#45860968625](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45860968625), on prior head `ffcdcdbb2ac7230f489ee9f5a70ebaf9d2ce85fd`, exposed one compile error in the new BUnit test:

- `IRenderedComponent<TaskPipelinePanel>.FindComponent<MudAlert>()` is not available in the repository's pinned bUnit `1.40.0` API. The Linux compiler reported `CS1061` at `TaskPipelinePanelTests.cs:91`.
- The production component and the test's user-visible pending text/UUID-redaction assertions were not implicated.
- Replaced the unsupported component lookup with the existing, supported DOM query `cut.Find(".mud-alert")`. This asserts that the pending informational alert is rendered without coupling the test to a MudBlazor severity-specific CSS class. The component's own `LandingFailureSeverity` maps pending/enqueued codes to `Severity.Info`; no production behavior changed.

### Validation

- Run #45860968625 is on the previous source head and is not valid for this fix. Linux completed with the BUnit compile error; Windows and macOS were still executing the old source when last polled.
- Current source head after this test-only fix: `99d90d9798a43c86458b686852731b36d19c49ed`.
- A fresh full Linux/Windows/macOS CI run is required on the exact post-journal head. No local .NET build is claimed; this environment has no .NET SDK.

### Next

1. Trigger and inspect CI on the post-journal head.
2. Check the Windows/macOS results from the prior run for any errors not visible in Linux's completed log.
3. Continue only after the current source compiles and the seven test modules, TRX guards and format checks pass.


## 2026-11-26 — Correct second unsupported bUnit API call

**Prior failing head:** `c25fe43bcfb59d4c8f0c102a0d72c78ccde791e2`  
**Fix commit:** `f3a16483b88e53d3831e975002641ebf57216a05`  
**CI:** [#45862576833](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45862576833)

### Finding

The earlier test-only correction replaced `FindComponent<MudAlert>()` with `cut.Find(".mud-alert")`. Both APIs are unavailable on this repository's typed `IRenderedComponent<TaskPipelinePanel>` with pinned bUnit `1.40.0`. CI failed to compile `ProjectBeacon.Web.Tests/Bunit/TaskPipelinePanelTests.cs:91` on Linux, Windows and macOS.

The Linux Web test and TRX guard failures were secondary consequences: the Web test assembly did not compile, so the expected seventh TRX report was not produced. No production-code failure was implicated in this specific incident.

### Correction

Removed the DOM/CSS existence assertion instead of replacing it with another unsupported bUnit API. The test still asserts both relevant user-visible contracts:

- the pending GitHub merge status message is rendered;
- the internal merge-request UUID is not exposed in the rendered markup.

This avoids coupling the test to a MudBlazor CSS class or DOM-query extension. No production UI behavior changed.

### Validation

- The fix is committed on `feat/release-management` as `f3a16483b88e53d3831e975002641ebf57216a05`.
- CI #45862576833 tested the previous head and is not evidence for this fix. Linux and Windows jobs failed Build because of the same unsupported `Find` call; Linux also failed the Web/TRX follow-on steps because the Web test report was absent. macOS had the same Build failure and was still completing subsequent jobs at the last inspection.
- A fresh full Linux/Windows/macOS CI run is required on the exact post-journal head. No local .NET build is claimed; .NET SDK is unavailable in this execution environment.

### Next

1. Trigger and inspect full cross-platform CI on the exact current branch head.
2. Do not add another bUnit DOM helper assertion unless its availability is verified against the pinned package.
3. Keep PR #230 open and unmerged until exact-head CI and disposable-repository merge-queue acceptance are complete.


## 2026-11-27 — Close .NET/Python version-source parity gaps

**Implementation branch:** `feat/release-management`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)  
**Source/test head before this journal entry:** `a1a3361ae96332345f9bd3c0f0c04722ff1fd428`

### Findings

A final parity pass over Version Policy exposed two remaining cases where the API could accept a source configuration that the tag-triggered Python release gate would interpret differently or reject:

1. **Regex dialect drift:** the .NET policy validator used a short list of rejected escapes and group prefixes. It could therefore accept .NET-only escapes such as `\0`, `\a`, `\x`, and `\p`, while the release workflow deliberately rejects them. The Python validator already used an explicit portable grammar.
2. **TOML scalar drift:** the .NET Tomlyn reader accepts only strings and integral numeric values, while the Python `tomllib` reader also accepted floats. A TOML float could therefore pass the API projection but fail (or produce different textual normalization) in the release workflow.

### Changes

- Replaced the ad-hoc .NET Regex denylist with a deterministic scanner aligned to the Python release-gate grammar:
  - exactly one capture group (named `(?<version>...)` or ordinary group 1);
  - non-capturing groups and positive/negative look-ahead only;
  - portable escapes only (`\d`, `\D`, `\s`, `\S`, `\t`, `\r`, `\n`, `\f`, `\v`, `\A`, `\z` and escaped regex punctuation);
  - optional named `\k<version>` backreference only after its named capture;
  - rejects look-behind, inline options, word-boundary/word-class escapes, Unicode categories, numeric backreferences, nested/subtractive character classes and all unsupported escapes.
- Python TOML projection now accepts the same scalar types as the .NET projection: strings and integers only. Floats, booleans, arrays, tables and date/time values are rejected before version normalization.
- Added Domain tests and Python release-gate tests for accepted/rejected Regex grammar and TOML scalar values.
- Updated Project Version source help in all five locales (en/ru/de/ja/zh) so users see the portable Regex grammar and TOML scalar rule while configuring a project.

### Validation status

- The last completed fully green CI [#45864599646](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45864599646) was run on head `149548ceab55ccfdd49eccec8381c3f0fce3fc65`. It predates the TOML/Regex parity commits and does **not** validate this work.
- Current source/test head before this journal update is `a1a3361ae96332345f9bd3c0f0c04722ff1fd428`; no CI run is currently associated with that SHA.
- No local .NET build is claimed; the .NET SDK is unavailable in this execution environment. Exact-head Linux/Windows/macOS CI is required.

### Remaining blockers

1. Run full cross-platform CI on the post-journal head and fix any compile, test, localization/resource or formatting failures.
2. Finish live GitHub merge-queue acceptance in a disposable repository, including request expiry, recovery via live GraphQL queue entry, queue removal, closed-unmerged PR, direct/queued merge completion and PR/head/base drift.
3. Classic `v*` tag protection and GitHub Environment `production` required reviewers remain unverified through the connector. No production tag or deployment is permitted until an administrator checks GitHub Settings.
4. Keep PR #230 open and unmerged until the current CI and live merge-queue acceptance are complete. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Fix portable Regex validator compile failure

**CI:** [#45898337610](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45898337610)  
**Failing exact head:** `cf919a332fb4df18f7be80eb18735b4cfb7ede65`  
**Current implementation head before this journal update:** `3f89c04df347cdc87a1aa5b64185fa3fef424285`

### Finding

The first exact-head cross-platform run after the TOML/Regex parity work failed to compile `ProjectBeacon.Domain/Entities/Projects/ProjectVersionPolicy.cs` on all three platforms. Linux, Windows and macOS all reported the same root diagnostic at line 386:

- `CS1010: Newline in constant`
- `CS1012: Too many characters in character literal`
- `CS1002: ; expected`

The remaining diagnostics later in the file, and the test modules reporting that their binaries could not be started / zero tests ran, were cascading effects of the Domain project failing to compile. They were not independent test failures.

### Correction

- Replaced the problematic C# backslash character literal in the portable Regex scanner with an integer code-point comparison (`(int)character == 92`), and handled escaped backslashes similarly.
- Rewrote the helper into small, explicit branches for the accepted escape set, character classes, group extensions and the single required version capture. This avoids the malformed literal and keeps the server-side grammar aligned with Python.
- Removed dead validator state and restored normal method separation/formatting.
- No release/merge behavior changed in this corrective slice.

### CI result and next verification

- Run #45898337610 tested the old exact head `cf919a33`; Linux, Windows and macOS all failed Build on the same C# character-literal error. Downstream test/TRX steps on Linux and platform test modules were cascades because the test binaries were not produced.
- The source fix is committed on `feat/release-management`; current head before this log entry is `3f89c04df347cdc87a1aa5b64185fa3fef424285`.
- The old CI run is not evidence for this fix. Trigger a new full Linux/Windows/macOS run on the post-journal head. No local .NET build is claimed; the .NET SDK is unavailable in this execution environment.

### Remaining blockers

1. Complete exact-head CI on the fixed Regex scanner; then handle any genuine tests/format findings separately from compiler cascades.
2. Perform live async merge-queue acceptance in a disposable GitHub repository, including pending expiry, live GraphQL queue-entry recovery, queue removal, closed-unmerged PR, direct/queued merge completion, PR/head/base drift and post-merge-check failure.
3. Classic `v*` tag protection and GitHub Environment `production` required reviewers remain unverified through the connector. No production tag/deploy is permitted before administrator verification.
4. Keep PR #230 open and unmerged until the exact-head CI and live merge-queue acceptance are complete.


## 2026-11-27 — Remove duplicated policy-catalog tail after class close

**CI:** [#45898748221](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45898748221)  
**Failing exact head:** `f24e8f158a985d59fe7a011b8ab3fe1563c76f91`  
**Current source head before this journal entry:** `6efd63f9982dec21cf6d99789af316471043c008`

### Finding

After the prior correction removed the malformed character literal, the next cross-platform run exposed the remaining structural defect: `ProjectVersionPolicy.cs` contained a complete implementation of `ProjectVersionPolicyCatalog` ending at line 623, followed by a stale duplicate fragment from an earlier helper edit and a second copy of the remaining private methods. The duplicate content was outside the already-closed class and caused `CS1022`, `CS0116`, and cascading namespace/member errors beginning at line 624.

- Linux and macOS reported the same duplicate-tail diagnostics.
- Windows reported the same diagnostics while its job was still running; the source error is platform-independent.
- The earlier character-literal compile error was gone. The new failure was a separate duplicate-source-tail issue.
- Downstream test modules could not start because the Domain assembly did not compile; their missing-binary/zero-test messages were cascades, not test assertions failing.

### Correction

- Removed the stale duplicate block after the first complete class close.
- Retained one complete class containing exactly one copy of the portable Regex validator, Calendar/tag/path validators and XML/JSON/TOML/Regex projection readers.
- Confirmed the resulting file has one copy of each private helper and ends immediately after the class closing brace.
- No release state, merge API or runtime workflow behavior changed.

### Validation status

- Run #45898748221 is an old-head failure on `f24e8f15`; it does not validate this duplicate-tail removal.
- Current implementation source head before journal update: `6efd63f9982dec21cf6d99789af316471043c008`.
- A fresh exact-head cross-platform CI run is required after this journal entry. No local .NET build is claimed because the SDK is unavailable in this execution environment.

### Remaining blockers

1. Run Linux/Windows/macOS CI on the post-journal head and address any remaining compiler, test, TRX, resource or formatting findings.
2. Finish live async merge-queue acceptance in a disposable GitHub repository, including request expiry, live GraphQL queue recovery, queue removal, closed-unmerged PR, queued/direct merge completion, PR/head/base drift and post-merge-check failure.
3. Classic `v*` tag protection and GitHub Environment `production` required reviewers remain unverified through the connector; no production tag or deployment is permitted before administrator confirmation.
4. Keep PR #230 open and unmerged until exact-head CI and live merge-queue acceptance are complete.


## 2026-11-27 — Exact-head cross-platform CI is green

**Validated source head:** `e87f4e6fc962b82102957b6d48c4ea7eac45f475`  
**CI:** [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564)  
**Result:** all Linux, Windows and macOS jobs completed successfully.

### Exact verification

- **Linux** — all 24 steps succeeded:
  - Python `Test release manifest tag policy` passed.
  - Restore, Build and EF `has-pending-model-changes` passed (diagnostic migration step skipped because no model drift was found).
  - API, Application, CLI, Domain, Infrastructure, Web and Worker test modules passed.
  - TRX result-count/allowed-skip guards passed.
  - `dotnet format ProjectBeacon.sln --verify-no-changes --exclude-diagnostics xUnit1051` passed.
- **Windows** — all 17 steps succeeded:
  - Restore, Build, EF model-drift check, all seven .NET test modules and `dotnet format` passed.
- **macOS** — all 16 steps succeeded:
  - Restore, Build, EF model-drift check, all seven .NET test modules and `dotnet format` passed.
- Windows/macOS intentionally run with `BEACON_TEST_SKIP_POSTGRES=1`; the PostgreSQL persistence lane is exercised by the Linux service-container job. These skips are not counted as database integration success on Windows/macOS.
- No CI failures, no pending model changes, no TRX guard failures and no formatting differences remain on this exact source head.

### What this confirms

- The malformed C# character literal and duplicated source tail have been removed; the full solution builds on all three operating systems.
- The new Domain tests for the portable Regex grammar and TOML scalar parity pass.
- The Python release-gate tests for Regex/TOML parity and the bounded regex worker pass.
- The localized Project Version resource changes compile across the platform matrix.

### Remaining blockers / next work

1. Live async GitHub merge-queue acceptance is still required in a disposable repository: pending→enqueued, 24-hour async UUID expiry/recovery from live GraphQL queue entry, queue removal, direct and queued merge completion, closed-unmerged PR, PR/head/base drift, and post-merge ReviewCheck failure. Unit tests and this CI do not replace live acceptance.
2. Classic `v*` tag protection and GitHub Environment `production` required reviewers remain unverified through the connector. An administrator must confirm these in GitHub Settings before any production tag/deployment.
3. Continue user-project acceptance of Release Management (project settings, role/RLS enforcement, failure UI, localization, candidate/publish/recovery states).
4. PR #230 remains open and unmerged until live merge-queue acceptance and publication protection checks are complete. No production tag/deployment was created. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Align JSON version-source readers with RFC 6901

**Implementation source head before this journal entry:** `74486e8517136165e2705b948cb6e2e50895a0e7`  
**Current PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)

### Finding

The previous commit tightened the .NET policy validator to RFC 6901, but the tag-triggered Python release gate still used permissive string replacement for JSON Pointer tokens and Python's Unicode-aware `str.isdigit()` for array indexes. Consequences:

- A malformed escape such as `~2` could be accepted by the release gate when a JSON property happened to have that literal name, even though Beacon's policy API rejected it.
- Unicode numerals could be accepted as Python array indexes but rejected by the .NET candidate/version reader.
- The valid RFC 6901 empty pointer (the document root) was rejected by both policy validation and the release workflow.

### Changes

- `ProjectBeacon.Domain/Entities/Projects/ProjectVersionPolicy.cs`: the empty selector is now accepted only for JSON sources, as RFC 6901 defines it as the pointer to the whole document. XML/TOML/Regex still require non-empty selectors.
- `scripts/verify_release_manifest.py`: added strict token decoding; only `~0` and `~1` are accepted. The root pointer `""` is supported. Array indexes now use ASCII decimal digits only, permit `0` or non-zero-leading decimal strings, and reject empty, signed, Unicode and leading-zero indexes.
- `ProjectBeacon.Domain.Tests/ProjectVersionPolicyCatalogTests.cs`: covered root-pointer policy validation and extraction, escaped slash/tilde object keys, malformed escapes, leading-zero/Unicode/out-of-range array indexes.
- `scripts/test_verify_release_manifest.py`: added equivalent root/escape/array-index tests for the Python release gate.

### Validation

- Source and regression tests are committed on `feat/release-management`; current implementation head before this journal update is `74486e8517136165e2705b948cb6e2e50895a0e7`.
- The last green exact-head CI is [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564) on `e87f4e6fc962b82102957b6d48c4ea7eac45f475`. It predates the JSON Pointer fixes and does not validate them.
- No local .NET SDK/build is available in this execution environment. A new CI on the post-journal head is required; do not treat the earlier green run as validation of these changes.

### Branch integration state

- `main` has advanced to `ed4dace38357f5062478ba3322148ab352d916ce` with the UI-readable-errors change and version `0.7.5.10`.
- The implementation branch is one commit behind `main` and GitHub currently reports PR #230 as not mergeable. The main commit changes several UI/resource files also touched by Release Management. Do not force-update the branch or overwrite those files; after CI, perform a deliberate three-way integration of the latest `main` changes and rerun the full matrix.
- The plan remains on its separate documentation branch. This development branch is not merged to `main`.

### Remaining blockers / next

1. Run exact-head Linux/Windows/macOS CI after this journal commit; fix all failures before more source edits.
2. Integrate `main` commit `ed4dace3` into this branch with a real three-way merge, preserving both UI error handling and Release/Roadmap changes.
3. Complete live async GitHub merge-queue acceptance in a disposable repository (pending→enqueued, 24-hour pending UUID expiry and GraphQL queue-entry recovery, queue removal, direct/queued completion, closed-unmerged PR, PR/head/base drift and post-merge check failure). Current unit tests/CI do not replace the live exercise.
4. Administrator verification of classic `v*` tag protection and GitHub Environment `production` required reviewers remains outstanding. No production tag or deployment.
5. Continue independent user-project acceptance for RLS/roles, localization, UI failure states, Release Candidate/Publish/recovery.


## 2026-11-27 — JSON Pointer parity and expiry-safe GitHub async landing recovery

**Implementation source head before this journal entry:** `a6efad9df6a61d0c279e931fe7b93e5cd06db3c0`  
**PR:** [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230)

### Version-source parity: RFC 6901

The .NET policy reader and Python tag-triggered release gate now use the same JSON Pointer rules:

- Empty pointer `""` selects the entire JSON document. The policy allows an empty selector only for Json sources.
- Only `~0` and `~1` escapes are accepted; trailing `~` and other escape codes fail validation/extraction.
- Array indexes use ASCII decimal digits, are non-negative, and cannot contain leading zeroes except the single index `0`. Empty, signed, Unicode-digit, and out-of-range indexes are rejected.
- Regression tests cover root JSON strings, escaped `/` and `~` object keys, malformed escapes, and array-index parity.
- The C# root fixture was corrected to contain a valid JSON string literal rather than a plain text token.

### Expired GitHub async merge requests

The previous AutoSync implementation correctly recovered an expired 24-hour request UUID when GraphQL still showed a live `mergeQueueEntry`, but it left an attempt in `pending` indefinitely when the exact UUID returned 404 and the still-open PR had no queue entry.

Implemented a fail-closed terminal outcome:

- The server now includes the accepted-at timestamp (`LandingAttempt.UpdatedAt` recorded when GitHub accepted `pending`) in the AutoSync target DTO. No database migration is needed.
- The pinned workstation classifies only an explicit HTTP 404 as a missing/expired async UUID. HTTP 403, rate limiting, timeouts, malformed responses, or GraphQL errors remain unknown/retryable and do not fail the landing.
- After 24 hours, the client queries GraphQL. It reports `GitHubAsyncMergeFailed` only when the exact PR is still OPEN and GraphQL confirms `mergeQueueEntry` is null. A present queue entry is still recovered as `enqueued`; an entry that is already known enqueued continues to use the separate queue-removal logic.
- The existing device-authenticated server failure handler revalidates the exact landing attempt, request UUID, PR/source/base and Review proof before moving ExecutePending to Failed. The task remains InProgress/Approved; no landing success proof is written and the task is not closed.
- Added CLI tests for the exact 24-hour boundary and for HTTP 404 vs 403/429/timeout classification; added application coverage that the pending acceptance timestamp is exported in the AutoSync DTO. AutoSync diagnostics now say the merge request did not complete, rather than incorrectly claiming every failure is a queue rejection.

### Verification state

- Changes are committed on `feat/release-management`; source/test head before this journal update is `a6efad9df6a61d0c279e931fe7b93e5cd06db3c0`.
- The last green CI is [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564) on `e87f4e6fc962b82102957b6d48c4ea7eac45f475`. It predates the JSON Pointer, expiry-recovery, and test-fixture changes.
- The Actions run list currently contains no run for `a6efad9d`; the commit combined-status response has no statuses. This repository's CI does not run on PR `synchronize`, and this GitHub connection exposes no workflow-dispatch write action. Do not claim the current source head is CI-green. Local .NET SDK/build is unavailable in this environment.
- A focused Python exercise of the strict JSON Pointer behavior passed for the root selector, escaped keys and invalid array indexes; this is not a substitute for repository CI.

### Branch integration and acceptance blockers

- `main` remains at `ed4dace38357f5062478ba3322148ab352d916ce` (readable UI errors, version `0.7.5.10`). The feature branch is `845` commits ahead and `1` behind; GitHub reports PR #230 as not mergeable. The main commit overlaps UI/resource files changed by Release Management. A deliberate three-way merge is still required; no forced ref update or overwrite was performed.
- Live GitHub merge-queue acceptance remains outstanding: async pending/enqueued, UUID expiry and GraphQL recovery, queue removal, direct/queued completion, closed-unmerged PR, source/base drift and post-merge check failure. Unit tests do not replace a real merge-queue run.
- Classic `v*` tag protection and GitHub Environment `production` required reviewers remain unverified. No production tag or deployment.
- Continue independent user-project acceptance for RLS/roles, localized settings and errors, Candidate/Publish/recovery. SaaS Control Plane and billing remain out of scope.

### Next

1. Obtain a full Linux/Windows/macOS CI run for the exact post-journal head.
2. Resolve the one-commit divergence from `main` with a real three-way merge and rerun CI.
3. Finish a controlled live merge-queue exercise in a disposable GitHub repository with required checks configured.
4. Have an administrator verify classic tag protection and production Environment reviewers before any publication test.


## 2026-11-27 — Integrate main UI error handling and version 0.7.5.10

**Merged upstream main commit:** `ed4dace38357f5062478ba3322148ab352d916ce`  
**Previous feature head:** `c4888617ae82777bc3987004057c123594a10a8c`

### Integration

- Integrated the latest `main` commit as a two-parent merge, keeping the release-management branch as the first parent and the upstream main commit as the second parent.
- Brought across all 16 upstream paths. Fourteen non-overlapping files are taken from the upstream commit unchanged; the two overlapping files were merged deliberately:
  - `ProjectManage.razor`: retained Version Policy and AutoSync settings and incorporated the project-description-history error state.
  - `Roadmap.razor`: retained Releases, Candidates, Hotfix and milestone-release association UI while incorporating project-load, Roadmap-load and milestone-history error states. Failed refreshes preserve the last successfully loaded collections; release-list failures are visible rather than silently clearing release data.
- Updated the application version through the upstream `Directory.Build.props` change to **0.7.5.10**.
- Kept the documentation and implementation branches separate. No SaaS Control Plane, billing, production tag or deployment work was included.

### Validation and remaining work

- The integration is recorded as a true two-parent merge commit; both parent SHAs remain visible in Git history.
- The owner reports CI green. The merged integration head still requires a fresh exact-head CI result before it is treated as validated.
- Live async GitHub merge-queue acceptance and administrator verification of classic `v*` tag protection and `production` Environment reviewers remain outstanding. PR #230 stays open and unmerged.


## 2026-11-27 — Roadmap release-list failure state

- Separated the release-list load error from milestone/task loading errors on the Roadmap page.
- A failed first load no longer renders the misleading `NoReleases` empty state; it shows the operation-specific warning instead.
- A failed refresh retains previously loaded releases and displays a warning. A successful reload clears that warning.
- No release publication, tag or deployment action was invoked.


## 2026-11-27 — Roadmap empty-state regression test

- The earlier release-list warning fix exposed a second branch in the page-level empty-state condition: with Release Management enabled, zero milestones and a failed initial release read could still bypass the release panel and hide the warning.
- Centralized the Roadmap empty-state predicate and made it account for both roadmap-section and release-list failures.
- Added Web test coverage for a failed initial release load, a successful empty release list, and another Roadmap load failure.
- This is a UI-state fix only; release candidate, tag and deployment behavior is unchanged.
- The previous CI run (#45949826652) is green on the exact pre-fix head `fbd5d7d84a9e4aef6a0c66664316b0a280eb6d0b`; this new commit requires another exact-head CI run.


## 2026-11-27 — Exact-head CI verification

**Run:** [#45949826652](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45949826652)  
**Validated SHA:** `fbd5d7d84a9e4aef6a0c66664316b0a280eb6d0b`  
**Result:** success on Linux, Windows and macOS.

- Linux: release-manifest policy tests, restore, build, EF pending-model-change check, all seven test modules, TRX guards and formatting passed. The EF diagnostic step was correctly skipped because there were no pending model changes.
- Windows and macOS: restore, build, EF pending-model-change check, all seven test modules and formatting passed.
- This run validates the integration of main and the first Roadmap failure-state fix. It predates the subsequent empty-state regression fix and its new tests in commit `c9981c12fb37a05e5bc950c17f53f2ca67919974`; the new head requires another CI run.
- The repository's CI intentionally excludes PR `synchronize`. No PR merge is permitted on the basis of the earlier run alone.


## 2026-11-27 — Exact-head CI passed after Roadmap regression fix

**CI run:** [#45951725841](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45951725841)  
**Validated implementation head:** `7647e7539c65608c84545424638af2403952e090`  
**Result:** success on Linux, Windows and macOS.

- Linux completed release-manifest policy tests, restore/build, EF pending-model-change validation, all seven test modules, TRX guards and formatting.
- Windows and macOS completed restore/build, EF pending-model-change validation, all seven test modules and formatting.
- This run includes the Roadmap empty-state regression coverage from `c9981c12fb37a05e5bc950c17f53f2ca67919974`. It supersedes the preceding journal entry's statement that the new exact-head run was still required.
- The PR head at the time of validation was exactly `7647e7539c65608c84545424638af2403952e090`; the subsequent journal-only update does not change application code. Trigger CI again after this journal update because the workflow does not run on PR `synchronize`.
- Remaining acceptance blockers are unchanged: live async GitHub merge-queue testing in a disposable repository, administrator verification of classic `v*` tag protection and `production` Environment reviewers, and real-project acceptance of the Release workflow. No production tag/deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Re-run the cross-platform CI on the post-journal branch head.
2. Complete live merge-queue acceptance in a separate disposable repository with required checks configured.
3. Have a repository administrator verify classic tag protection and `production` Environment required reviewers.
4. Continue user-project acceptance for roles/RLS, localized settings and failures, Candidate/Publish/recovery states.


## 2026-11-27 — Add Release Prepare/Verify/Publish handler guard tests

**Test source commit:** `d2e309585b8e041be61ec7a25bdc86fb2f124b53`

- Added six application-handler regression tests:
  - Prepare rejects a project Member and leaves preparation NotStarted.
  - Prepare rejects a project without configured version-file projections and queues no workstation command.
  - VerifyLanding rejects a Member and queues no workstation command.
  - VerifyLanding rejects a Release that has not reached Prepared/VerifyFailed.
  - Publish rejects a Member.
  - Publish rejects a Planned/non-Candidate Release and queues no workstation command.
- These tests exercise authorization and lifecycle barriers before workstation side effects; they do not create tags, trigger deployment, or merge a PR.
- The green CI run #45951725841 validates the immediately preceding application head `7647e7539c65608c84545424638af2403952e090`, but predates these newly added tests. The doc-only run #45953557893 targets journal head `77adf061f19d27a414799eb6c98e9be9525c0bae` and does not contain the test commit.
- A fresh exact-head cross-platform CI run is required after this test and journal update. The workflow intentionally excludes PR `synchronize`; do not infer test success from either earlier run.
- No product code behavior changed. The branch remains isolated; no SaaS Control Plane, billing, production tag, deployment, or PR merge.

### Next

1. Run Linux/Windows/macOS CI on the exact branch head containing these six tests and fix any compile/test failures.
2. Add further handler/completion integration coverage only where current tests expose a specific gap.
3. Complete live GitHub merge-queue acceptance in a separate disposable repository; verify publication protection settings with a repository administrator.


## 2026-11-27 — Harden ReleasePrepare manifest path and align release CI gates

**Implementation commits**
- `03647840f8fd5f404094dcf8606463bc9f2da293` — reject repository-controlled symlinks/junctions at the Release manifest destination.
- `8ac58c314c8394217fa8b9f47e733f064daf9db4` — add ReleasePrepare manifest path regression tests.
- `a16a69dbdd16d6e40f96dbdb1ed694942fd85763`, `e8940dc7c1cd491ecb7c4441b29b8e7179f08721`, `4cfcad84542168ef1ffb0664bf08c33f15a182ab` — align CI/release TRX result floors and diagnostics.

### Changes

- ReleasePrepare now resolves `.beacon/release.json` only after checking the release worktree, `.beacon` directory and manifest file for symlinks/junctions and verifying their resolved paths remain inside the release worktree.
- The shared reparse-point check now detects dangling symbolic links through `FileSystemInfo.LinkTarget`; `File.Exists` and `Directory.Exists` alone do not detect those.
- Added three CLI regression tests: valid in-worktree manifest path, rejection of a `.beacon` directory symlink, and rejection of a dangling `release.json` file symlink.
- Together with the six preceding Prepare/Verify/Publish application-handler tests, the changed test suite adds nine facts.
- The preceding green Linux CI run reported 2,753 test results. The cross-platform CI and tag-triggered release workflow now both require at least 2,762 Linux TRX results, and their complete TRX guard blocks are identical. The skip-diagnostic newline handling was also aligned.

### Validation state

- The earlier green CI #45951725841 validates the pre-regression-test implementation head only; it does not validate these new tests or the symlink hardening.
- The intermediate CI run #45954279454 targets head `9e62bb971d2561ebd7fb1b065efde56a9e80ed91`, so it does not include the symlink hardening or the updated release gate. It must not be treated as validation of the current implementation.
- A new full Linux/Windows/macOS CI run is required on the latest PR head after this journal update. No production tag/deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Run full CI on the exact latest PR head; verify all nine added tests are included and the Linux TRX result count reaches the new floor.
2. Complete live GitHub merge-queue acceptance in a disposable repository.
3. Have a repository administrator verify classic `v*` tag protection and `production` Environment required reviewers.
4. Complete user-project acceptance for the release workflow and deployment recovery paths.


## 2026-11-27 — Fix ReleasePrepare manifest path compile regression

**Implementation commits**
- `d5dcfbbeebdd4a4771e53e24b0f772b34127b777` — retain the resolved manifest path as a string for both write and hash operations.
- `13d40a2bb846e755014b01abe45389797b91675d` — canonicalize temp worktree paths in symlink tests so macOS `/tmp` → `/private/tmp` resolution does not mask the intended in-worktree symlink assertions.

### CI diagnosis

- Run [#45954916231](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45954916231) targeted previous head `fe6f3b637c99a4cacdd84019fbd2d651319c7da4`.
- Build failed consistently on Linux, Windows and macOS with `CS1503` at `ReleasePrepareExecutor.cs:247`: the new tuple `(string? Path, string? Error)` was passed directly to `File.ReadAllBytesAsync`.
- Fixed by extracting `manifestPathResult.Path` after checking its error, then using the resolved string for both `WriteAllTextAsync` and `ReadAllBytesAsync`.
- The CLI test step also failed because the solution build had not produced `ProjectBeacon.Cli.Tests.dll`; that was a downstream consequence of the compile failure, not a separate test assertion result.
- Adjusted the symlink tests to use the canonical physical paths from `AgentTaskWorktree.ResolvePhysicalPath` before calling the helper. This avoids treating macOS' temporary-directory alias as the symlink under test.
- The current fixes have not yet been CI-validated. The next run must target the post-journal branch head exactly.

### Next

1. Run Linux/Windows/macOS CI on the new exact head and inspect every job, including the Linux 2,762-result TRX gate.
2. Resolve any remaining compile/test/format issues before considering the branch ready for merge.
3. Live merge-queue acceptance and publication-protection verification remain outstanding; no production tag, deployment, merge, SaaS Control Plane or billing changes.


## 2026-11-27 — Exact-head cross-platform CI passed after ReleasePrepare path hardening

**Run:** [#45955852379](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45955852379)  
**Validated implementation SHA:** `e02e4d7579c3251e7b6ee5b82c8eaf7fc4611ad9`  
**Result:** success on Linux, Windows and macOS.

- All three platforms passed restore, build, EF pending-model-change validation, all seven test modules and formatting.
- Linux authoritative lane passed all seven modules and its strict TRX guard: **2,762 results**, one expected platform-conditional skip, zero failures; both symlink security gates passed.
- The new Prepare/Verify/Publish handler tests and the ReleasePrepare manifest-path symlink tests are included in this run.
- This supersedes the prior journal statement that the symlink hardening and updated 2,762-result gate still needed CI validation.
- The run validates the implementation SHA above. This journal-only commit does not alter application code, but a final run is being triggered on the post-journal PR head so that the latest GitHub revision itself has a green required check.

### Remaining acceptance blockers

1. Live asynchronous GitHub Merge Queue acceptance still needs a separate writable disposable repository with merge queue and required checks configured. Do not run destructive queue scenarios against ProjectBeacon itself.
2. Classic `v*` tag protection and required reviewers on the `production` Environment remain unverified: current GitHub integration cannot read the relevant settings with sufficient permissions. Tag and deployment publication stay disabled until an administrator verifies them.
3. Real-project end-to-end acceptance remains outstanding: prepare a release, merge through the selected single landing authority, verify the immutable Candidate, publish the tag, observe the release workflow and prove deployed version/SHA, including failure/recovery paths.
4. No production tag, deployment or PR merge was performed. SaaS Control Plane and billing were not touched.

### Next

1. Re-run exact-head CI after this journal-only commit.
2. Continue static review of the publication/deployment workflow and add focused regression tests only for confirmed gaps.
3. Complete live merge-queue and publication-protection acceptance when the required repository access is available.


## 2026-11-27 — Require ancestry-preserving merge strategy for release PRs

**Implementation commits**
- 1ad4fdddc082de8b3b7b3ed884226ca1c6d714be — request merge_method=merge for direct GitHub async merges while retaining merge_action=default for queue selection.
- 2b765b00d811db207c42f82a0f76df723a1b4db8 — assert the exact merge method in the async merge request regression test.
- 78b216b4c43c50fe129360b56c86c4c7597ab091 — document the required merge method in generated Release PRs and give a specific VerifyLanding failure for squash/rebase strategies.
- 08eebcc7a769ad9a2a23a74139f0f923d0bfc13a — clarify the release workflow's fail-closed diagnostic when the original preparation commit/trailer is absent.

### Why this is required

- ReleasePrepare persists the exact source commit and Beacon-Release: <releaseId> trailer. VerifyLanding requires that commit to be an ancestor of the remote target branch; the tag workflow independently locates the preparation commit and validates its trailer and changed-path scope.
- A local Git integration check confirmed that a merge commit preserves the original ReleasePrepare SHA in ancestry and git log -1 HEAD -- .beacon/release.json locates the original preparation commit. A squash commit creates a new one-parent commit and the original ReleasePrepare SHA is not an ancestor. Rebase similarly rewrites commit SHA identity.
- GitHub repository metadata currently allows merge commits, rebase and squash. The connected integration cannot read the branch protection / merge queue settings needed to see which merge method is configured for main.
- GitHub's asynchronous merge API accepts merge_method for direct merges; merge queues use their administrator-selected merge method. Therefore the request now explicitly asks for merge commits on the direct path, while the generated Release PR warns that any merge queue must be configured with its Merge method set to merge.
- VerifyLanding still independently checks ancestry and fails closed if the original ReleasePrepare commit is absent. The tag workflow also refuses to accept a squash/rebase rewrite without the original trailer; no tag or deployment is performed.

### Validation state

- The preceding exact-head CI #45955852379 passed on implementation SHA e02e4d7579c3251e7b6ee5b82c8eaf7fc4611ad9.
- The merge-strategy changes above are newer than that successful run and require a fresh exact-head Linux/Windows/macOS CI run.
- Live merge-queue acceptance remains blocked on administrator access to configure/verify the queue Merge method. Do not merge a Release PR through a queue until it is confirmed to use merge.
- No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Re-run the full cross-platform CI on the current head after this journal entry.
2. Verify that the repository's merge queue uses the merge method and complete the live queue acceptance in a disposable repository.
3. Continue release workflow end-to-end acceptance for Candidate, Publish, deployment verification and failure recovery.


## 2026-11-27 — Merge method and merge_group CI contract

**Implementation commits**
- 1ad4fdddc082de8b3b7b3ed884226ca1c6d714be — direct GitHub async merge requests explicitly request merge_method=merge while retaining merge_action=default.
- 2b765b00d811db207c42f82a0f76df723a1b4db8 — regression assertion for the requested merge method.
- 78b216b4c43c50fe129360b56c86c4c7597ab091 — document merge-commit requirement in generated Release PRs and provide a specific ancestry failure message.
- 08eebcc7a769ad9a2a23a74139f0f923d0bfc13a — clarify the release-manifest tag gate diagnostic.
- 033ae81861686a977b2071048b7dfdd6f67202a5 — add GitHub merge_group checks_requested event to CI.

### Merge strategy verification

- ReleasePrepare records the prepared commit SHA and Beacon-Release trailer. VerifyLanding requires that exact SHA to remain an ancestor of the remote target branch.
- A temporary Git integration exercise confirmed that a merge commit keeps the original ReleasePrepare commit in ancestry, while a squash commit replaces it with a new one-parent commit. Rebase also rewrites the commit SHA.
- Direct async merges now request merge_method=merge. GitHub documents merge_method as a direct-merge option; when merge_action=default selects a merge queue, the queue uses the administrator-selected Merge method.
- Generated Release PRs now explicitly require a merge commit. The merge queue must be configured with Merge method=merge. VerifyLanding remains fail-closed if the original prepared SHA is not in target-branch ancestry, so squash/rebase cannot produce a Candidate through Beacon.

### Merge queue CI integration

- GitHub requires Actions workflows to subscribe to merge_group/checks_requested for required checks to be reported on synthetic merge-group refs.
- Added this event to ci.yml. The existing full Linux/Windows/macOS jobs and check names are reused; concurrency falls back to github.ref for a merge_group run because there is no pull_request.number.
- The change is not covered by the running CI #45957896644 because that run targets the previous head 8ddd6f535eb5d3bab1886d8829ec9ffa5a4f9820. A new exact-head run is required.

### Environment limitation for live queue acceptance

- The repository API reports ProjectBeacon is public and owner_type=User, and the connected account has no organization memberships and only this one writable repository.
- GitHub's current documentation limits merge queues to public repositories owned by an organization, or private repositories owned by organizations on GitHub Enterprise Cloud. Therefore a live merge queue cannot be enabled/tested on the current personal-account ProjectBeacon repository under these conditions.
- The implementation plan was updated on the separate plan branch: docs/release-management-plan commit edcfe36f277258e49868b77e4afc4fcf131e5549.
- A separate writable org-owned disposable repository is required for live tests. Do not create a throwaway PR or simulate destructive queue behavior against ProjectBeacon itself.

### Validation and blockers

- Exact-head green CI #45955852379 validated implementation SHA e02e4d7579c3251e7b6ee5b82c8eaf7fc4611ad9 (all platforms; 2,762 Linux TRX results).
- CI #45957896644 targets SHA 8ddd6f535eb5d3bab1886d8829ec9ffa5a4f9820 and is still running; it includes the merge-method changes but predates merge_group trigger commit 033ae81861686a977b2071048b7dfdd6f67202a5.
- Classic v* tag protection and production Environment required reviewers remain unverified because the connected GitHub integration cannot read those settings. Queue Merge method=merge also needs administrator verification on an eligible org-owned test repository.
- No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Re-run Linux/Windows/macOS CI on the current post-journal branch head, including merge_group workflow syntax and the merge-method regression test.
2. Obtain an org-owned disposable repository with Merge Queue enabled and Merge method=merge; run pending/enqueued/expiry/removal/direct-merged/queued-merged/closed-PR/drift/post-merge-failure acceptance.
3. Verify classic v* tag protection and production Environment required reviewers with an administrator.
4. Complete the release workflow end-to-end in a real project, including Candidate, Publish, deployment proof and failure recovery.


## 2026-11-27 — Validate remote annotated-tag identity after concurrent push

**Implementation commits**
- 3f56a0516068de9e61e4dbe282498366bb25cac1 — verify remote annotated tag object identity during initial, concurrent-push recovery and final checks.
- a52a1d1a0ecbc99f7fa809695c2fc457bb1de748 — add three ReleasePublishExecutor tag-evidence regression facts.
- efc1fb9d34b0c527d61a7adf82ba5ed91e0b2957 and 06150e4c04804df643290b4dbf641b23b94de27a — raise both Linux TRX minimums from 2,762 to 2,765 to account for the three new facts.

### Issue and fix

- The tag push path correctly avoided force updates and recovered from a lost push acknowledgement by checking that the remote tag peeled to the Candidate SHA.
- However, in that concurrent-writer recovery path it did not re-read the remote annotated tag object. A tag with the right target commit but incorrect/missing Release/Candidate annotation could be accepted.
- Added remote tag verification through a unique temporary local ref under `refs/beacon/release-tag-verification`; it never overwrites the normal local tag ref. The fetched remote object must be an annotated tag with exact tag name, commit target, Release id, Candidate id, Candidate SHA and version identity lines.
- The published tag object's SHA is compared with the advertised remote tag object SHA. The same identity check now runs after an ambiguous concurrent push and during final remote verification.
- Identity lines are matched exactly and must occur once; substring matches and duplicate lines are rejected.
- Temporary refs are deleted after inspection. A cleanup failure cannot rewrite or alter the remote tag; future verification uses a different unique temporary ref.

### Regression tests and gates

- Added three CLI facts: accept exact annotated Candidate tag evidence; reject different target/name and lightweight tags; reject missing, altered, suffixed or duplicate identity lines.
- Increased CI and release.yml Linux TRX guard to 2,765; both thresholds must stay equal.
- This code/test/workflow update is newer than CI #45958653131, which tests pre-tag-race head `914756f88fe04336f81614f5a8a63348503c4f16`. A new exact-head run is required after the current journal/PR update.

### Remaining acceptance blockers

- Live Merge Queue acceptance requires a writable org-owned disposable repository. ProjectBeacon is public and owned by a personal GitHub account; the account has no organization memberships. The current repository cannot host a live merge-queue test under GitHub's documented eligibility rules.
- The CI `merge_group: checks_requested` trigger is now present, but live required-check behavior on an actual queue is not proven until the separate org-owned acceptance repository is available.
- An administrator must verify Merge Queue method=merge, classic `v*` tag protection and required reviewers for Environment `production`.
- No production tag, deployment or PR merge has been performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Run exact-head CI on the latest branch head, including the three annotated-tag identity facts and the 2,765 Linux TRX gate.
2. Complete live queue acceptance on a disposable org-owned repository with method=merge and required CI checks.
3. Finish publication/deployment end-to-end acceptance and recovery paths once administrator settings are verified.


### Follow-up — fail-closed remote listing diagnostics

- A local bare-remote integration exercise confirmed that fetching an annotated tag to a unique ref under refs/beacon/release-tag-verification works, returns the annotated tag object and peeled commit, and can be cleaned up without changing refs/tags.
- Added final fail-closed checks for malformed git ls-remote output after an ambiguous push and during final tag verification; partial parsed output is no longer sufficient evidence.
- Code commit: eb1033d9bc19f3dd091c74a43474b029d3a4e811.
- This change is newer than CI #45959467058 and requires a new exact-head full CI run. The prior run covers head e5ec37cda058b584c638a42cfe241acd6f8806fe only.


## 2026-11-27 — Pin publication retry after immutable tag

**Implementation commits**
- e569c12168bac0da273ca1a0eb75d74d6012ed41 — add a domain regression test for FailedAfterTag retry.
- 9b0054661751f96d7683b8a963c40eea622d4667 and 05a894353218347dd2e7791acbd559d39c919e5d — raise CI and release.yml Linux TRX floors to 2,766.

### Lifecycle contract

- Once the immutable tag has been pushed, publication failure must not allow the Release to be cancelled or retargeted. The publication may be retried to observe/recover the workflow/deployment, but only using the original Candidate/tag/version/deployment-endpoint payload.
- Added a Domain regression fact: create Candidate; queue publication; record FailedAfterTag with a workflow run; reject a different payload; accept retry with the exact original payload; preserve PublicationTagPushed and the prior workflow run id; advance publication revision and bind a new command id.
- CI and release.yml now require at least 2,766 Linux test results; both complete TRX blocks remain identical.
- The latest publication/tag changes are newer than the current CI run #45959876969, which targeted the previous head. A new exact-head run is required.

### Validation and external acceptance

- A local bare-remote exercise confirmed a tag can be fetched into a temporary refs/beacon/release-tag-verification ref, its annotated object and peeled commit inspected, and the temporary ref deleted without altering refs/tags.
- Exact remote tag object identity and malformed ls-remote responses are fail-closed in the current implementation.
- Merge Queue live acceptance still requires an org-owned disposable repository; the connected account has no org membership and ProjectBeacon is a public personal-account repository.
- Queue Merge method=merge, classic v* tag protection and production Environment required reviewers remain administrator verification items.
- No production tag, deployment or PR merge. SaaS Control Plane and billing remain out of scope.


### Follow-up — identity lines are byte-exact

- Tightened the annotated tag identity helper to compare each required header/message line exactly, without trimming trailing whitespace.
- Added a regression assertion rejecting a Release identity line with a trailing space. This is an assertion within the existing tag-evidence fact; the three-fact TRX increase remains 2,766.
- Code commit: 2de0e5f3dba63c434430900bf6598f82d6c2d17f.
- Test commit: aa85877a7baffdab021fc8833b40ec00e453d27e.
- The current CI run was created for an earlier head. A new exact-head run is required after this journal commit.


## 2026-11-27 — Fail-closed tag metadata and publication completion evidence

**Implementation commits**
- a633b755aa205be53a7fb583ddd169817fb1b965 — preserve CR bytes during tag identity parsing and clean temporary verification refs even when fetch is cancelled.
- 123745108c8f9668b1ef35f906be87dee19ca0f0 — reject a carriage return appended to an exact Release tag identity line.
- 62b9e67c52f4044c520c51b68d3c967d83456987 and 2555277d727c4ba884eb24446651aa79b5eb6b6a — treat incomplete/mismatched Publish failure evidence as “tag may have been pushed”; only an explicit typed false bound to the exact project/Release/Candidate/version/tag/SHA may produce FailedBeforeTag.
- 2aaf5d597c9d2b0f13f4cfe6d0bd94495e800380 — regression test for missing, malformed and mismatched tag-pushed evidence.
- 21149f8b7534b96365a97b27c345284c73faaa24 and e99a96d31dfd8d1f3306634b511c33e8154e283c — synchronize CI and release.yml Linux TRX floors at 2,767.

### Details

- Git tag objects use LF delimiters. The identity parser previously split on both CR and LF, which could discard a carriage return before exact line comparison. It now splits only on LF; CR remains part of the line and causes a mismatch. Added a trailing-CR assertion alongside the existing exact-identity test.
- Remote tag verification fetches into a unique local-only ref. Cleanup now runs from a finally block that covers the fetch itself and uses CancellationToken.None, so request cancellation cannot interrupt deletion of a ref already written by Git.
- Server completion previously treated a valid JSON object with a missing `tagPushed` field as false. That could incorrectly mark a publication as FailedBeforeTag even when a tag may already exist. The server now accepts false only with a typed boolean and a matching Publish result identity (project, release, candidate, version, tag and candidate SHA). Missing, malformed or mismatched evidence conservatively records FailedAfterTag and requires retry with the original immutable payload.
- Added one Application test fact for the publication-completion evidence validator; the CLI tag test adds an assertion inside an existing fact. Linux result minimum is now 2,767 in both workflows; their full TRX guard blocks were checked identical.

### Validation

- The immediately preceding exact-head CI [#45960317995](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45960317995) passed on SHA `85b83094c168e59d9e2359faa72ba1f34707eeab`: Linux, Windows and macOS all green; Linux TRX 2,766 results, 7 modules, one expected platform-conditional skip, symlink gates Passed.
- The code/test/threshold commits above are newer than that run. A new exact-head CI run is required on the post-journal head; do not treat #45960317995 as validation of these additional changes.
- Live Merge Queue still requires a writable org-owned disposable repository. Queue Merge method=merge, classic `v*` tag protection and `production` Environment required reviewers still need administrator verification.
- No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Run exact-head Linux/Windows/macOS CI on the current post-journal head and resolve any compile, test, TRX or formatting failures.
2. Complete live merge-queue acceptance in a writable org-owned disposable repository with Merge method=merge.
3. Have an administrator verify tag protection and production Environment reviewers.
4. Complete real-project Candidate/Publish/deployment and failure-recovery acceptance before considering PR #230 for merge.


## 2026-11-27 — Fix publication evidence helper scope after CI compiler failure

**CI diagnosis:** [#45962310328](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45962310328) targeted SHA `1db2e3d2a42e435b9ac0671d0d2742b431980559` and failed Build on Linux/macOS with two `CS0103` errors: `IsPublicationResultForPayload` and `ReadTagPushedEvidence` were not in scope for `ReleasePublishCompletion`. The test projects then failed because the solution build had not produced their assemblies; those were downstream failures, not test assertion failures.

**Correction:** commit `aa50996534283f5b099e91be657dd378cb8d9f29`.
- Moved `ReadTagPushedEvidence`, `IsPublicationResultForPayload` and its typed-boolean helper into `ReleasePublishCompletion`, where the caller and the regression test can access the internal entry point.
- Removed the accidentally duplicated helpers from `ReleasePrepareCompletion` and restored its own direct `ReadBool` implementation.
- Confirmed by source inspection that the helpers are now inside the Publish completion class, and the application test references the internal method through the existing InternalsVisibleTo declaration.

No additional test fact or TRX threshold change: CI/release.yml remain synchronized at 2,767. The current fix and this journal entry require a new exact-head CI run. Do not treat #45962310328 as a green run. No production tag, deployment, or PR merge.


## 2026-11-27 — Exact-head CI passed for annotated-tag and publication evidence hardening

**CI run:** [#45962729055](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45962729055)  
**Validated SHA:** `841dd90bbd1e5375558acb20d2b0515c828876d4`  
**Result:** Linux, Windows and macOS all succeeded.

- All three jobs passed restore/build, EF pending-model-change validation, all seven test modules and formatting.
- Linux TRX guard: **2,767 results**, seven modules, one expected platform-conditional skip, zero failures; both symlink security gates passed.
- The run includes the exact tag-object identity changes, LF-only parsing that preserves CR bytes, cleanup of temporary verification refs even after fetch cancellation, server-side fail-closed Publish completion evidence, and the regression test for missing/malformed/mismatched `tagPushed`.
- `.github/workflows/ci.yml` and `.github/workflows/release.yml` both require a minimum of 2,767 Linux results; the complete TRX guard blocks are byte-identical.
- CI #45962729055 supersedes earlier incomplete/cancelled runs for these changes. The previous Build failure on head `1db2e3d2` was a compile-scope error in the publication evidence helpers; the helper-scope correction in `aa50996534283f5b099e91be657dd378cb8d9f29` is included and validated by this run.

### Publication evidence review

- `ReadRemoteTagEvidenceAsync` places remote fetch inside a `try/finally`; cleanup uses `CancellationToken.None` so cancellation cannot interrupt deletion of a partially-created local verification ref.
- The unique temporary ref lives only under `refs/beacon/release-tag-verification`; it does not replace the local tag or mutate remote refs.
- Remote annotated-tag evidence is checked by tag object SHA and exact header/message lines (each required identity line exactly once, byte-exact including CR/trailing whitespace), plus exact peeled Candidate SHA.
- Server completion accepts `tagPushed=false` only when a complete Publish result matches the original project/Release/Candidate/version/tag/SHA payload and contains a typed boolean false. Missing, malformed or mismatched evidence is treated as a possibly-pushed tag and remains recoverable only through the original immutable Candidate payload.
- Confirmed successful publication additionally requires exact Release/Candidate identity, immutable tag name/SHA, completed successful release workflow, and production version/gitSha equality.

### Remaining acceptance blockers

1. Live Merge Queue acceptance requires a writable org-owned disposable repository. ProjectBeacon is a public repository owned by a personal account; the connected account has no organization membership. `merge_group: checks_requested` is now present in CI, but a live queue event and required-check behavior remain unverified.
2. In an eligible org-owned test repository, Merge Queue method must be configured to `merge` to preserve the exact ReleasePrepare commit SHA in target-branch ancestry. Direct async merges explicitly request `merge_method=merge`.
3. A repository administrator must verify classic `v*` tag protection and required reviewers for the `production` Environment.
4. Real-project end-to-end acceptance remains: ReleasePrepare → ancestry-preserving merge → immutable Candidate → Publish/tag → successful Release workflow → deployed version/SHA, plus failure/retry cases.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Record the successful exact-head run in the separate release-plan document and PR #230.
2. Re-trigger CI for the post-journal PR head (the workflow excludes `pull_request.synchronize`).
3. Complete live queue/publication protection acceptance when an org-owned test repository and administrator access are available.


## 2026-11-27 — Least-privilege permissions for the Release workflow

**Implementation commit:** `e1100e783b56100228a435b8b42ff753731dfb47`

### Finding

The Release workflow had workflow-level `packages: write`. That permission was therefore inherited by jobs that only run tests, inspect images or deploy. Only image publication needs to write packages; the scanner only needs to pull/read the package.

### Change

- Workflow-level permissions are now only `contents: read`.
- `client` retains job-scoped `contents: write` because it creates/uploads GitHub Release assets.
- `image` has job-scoped `contents: read` and `packages: write` for GHCR image publication.
- `scan` has job-scoped `contents: read` and `packages: read` for image inspection and Trivy scanning.
- `tests` and `deploy` inherit only workflow-level `contents: read`; neither receives package write access.
- No deployment secrets, SSH access, tag or environment configuration was changed.

### Validation

- Static check of the resulting workflow confirms that the only `packages: write` declaration is inside the `image` job; the only `packages: read` declaration is inside `scan`; and global permissions no longer include package write.
- The previous exact-head CI #45962729055 passed on application/workflow head `841dd90bbd1e5375558acb20d2b0515c828876d4` with 2,767 Linux TRX results. It predates this permission change.
- CI #45971210327 targets journal head `cc89faa0d3d0c078bad27711fb4dfc5d11628f81`; it also predates this permission change and is still running at the time of this entry.
- A new exact-head CI run is required on the post-journal head containing `e1100e783b56100228a435b8b42ff753731dfb47`.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Run exact-head Linux/Windows/macOS CI against the least-privilege workflow.
2. Review final release-workflow job permissions and tag/deployment gates after CI.
3. Complete org-owned Merge Queue live acceptance and administrator verification of `v*` protection / `production` reviewers when access is available.


## 2026-11-27 — Treat ambiguous tag push outcomes as potentially published

**Implementation commits**
- `8f9fece3675fedee0bb9b94e13a2454eef843316` — make initial and retry remote tag inspection fail closed; add conservative publication classification for the ambiguous push path.
- `d099ccedcc74c0c765f984196597e14b2408ce4c` — add a CLI regression fact for remote inspection success/failure, malformed listings, exact Candidate target, absence and conflicting target.
- `efd835e1eed944b8b6f9c9b102a7b158b022c3df` — keep helper calls style-consistent.
- `3ebaec14f7af24612a8f10bdf0f16b4bce9446fe` and `03fc6bbd933803a73452d8eab812ac5f6361ea07` — raise both Linux TRX floors from 2,767 to 2,768.

### Issue

After a tag push returned non-zero/timeout, a follow-up `ls-remote` failure or malformed response could be converted into a valid `tagPushed=false`. The server accepts an exact, typed false result as proof of `FailedBeforeTag`; that could allow Candidate cancellation even though the push may have succeeded and the remote could not be inspected.

A related path returned false when the initial remote inspection failed, even though the same immutable tag might already exist from a previously interrupted publication attempt. An existing remote tag that could not be fetched was also reported as pre-tag.

### Fix

- Added `MayHavePublishedTag`: it returns true whenever remote inspection failed, the listing is malformed, or the observed target equals the Candidate SHA. It returns false only when a successful, well-formed listing proves that the Candidate tag is absent or points elsewhere.
- Initial `ls-remote` failure and malformed listing now produce `tagPushed=true`; publication remains `FailedAfterTag` and can retry only with the original immutable Candidate payload.
- If the remote tag exists but cannot be fetched and verified, it is conservatively classified as potentially published.
- For an ambiguous push, a failed/malformed follow-up listing is now `tagPushed=true`. A successful well-formed listing that proves absence or a different target may safely return false. A listing that shows the Candidate SHA continues through annotated-tag object verification.
- When an existing remote tag resolves to the Candidate SHA but its annotated object differs from the local expected object, the result is still treated as potentially published.
- Added a regression fact covering failed inspection, malformed output, exact Candidate target, verified absence and a conflicting target.

### Validation

- Static review confirms all `tagPushed=false` paths in the ambiguous-push recovery now require a successful, well-formed remote listing that proves the Candidate target is absent/different.
- CI and release.yml Linux TRX gates now both require 2,768 results; complete guard blocks are byte-identical.
- Earlier exact-head CI #45962729055 passed on SHA `841dd90bbd1e5375558acb20d2b0515c828876d4`, but predates both workflow permission hardening and this new tag-push regression test.
- CI #45971667573 targets head `59752b2379457389ffe2b722f5764830011915bf`; it includes the least-privilege permissions but predates the ambiguous tag push fix and still has test modules running.
- A new exact-head CI run is required for the current post-journal head.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Run the exact current head through Linux/Windows/macOS CI and confirm Linux TRX reports at least 2,768 results.
2. Re-check all code paths that can set `tagPushed=false`; false must be backed by successful remote evidence.
3. Continue live Merge Queue and publication protection acceptance when an eligible org-owned disposable repository and administrator access are available.


### Follow-up — require complete annotated-tag ref evidence

**Implementation commits:** `36ee76e2891b54377e6be5c42c808cf60ad52ce1`, `3c06b5700a41f3f3c4bbde843224504ef0ddf0fc`  
**Test commits:** `a0b0ed13f74e29f48427db0bb7355205f07d403c`, `d285d2bf1722a94f0187e806d7723ec53554b4ae`

- Git's annotated tag listing has two relevant refs: the tag object ref and the peeled commit ref. The tag object's SHA is not the Candidate commit SHA.
- The previous classifier used the tag-object ref as a fallback "observed target SHA" when the peeled ref was missing. That could incorrectly prove absence for an annotated tag whose peeled ref was omitted from a partial response.
- `MayHavePublishedTag` now receives explicit presence flags for both refs. Failed/malformed listing, only one ref present, or a peeled SHA equal to Candidate means the tag may have been published. Only a successful well-formed listing with both refs absent, or both refs present with a different peeled target, can establish that this Candidate tag is not published.
- In the existing-tag path, when the peeled ref is absent, the already validated local annotated tag object may establish the target only if its object SHA exactly matches the advertised remote tag-object SHA.
- In ambiguous-push recovery, an incomplete ref pair triggers a fetch to the unique verification ref; the fetched annotated object and target are checked before accepting publication evidence.
- Final remote verification now uses the same two-ref classification and requires the tag ref plus a fetched annotated object whose object SHA matches the remote listing. It no longer compares an annotated tag-object SHA directly to a commit SHA.
- Extended the same CLI regression fact to cover missing tag ref, missing peeled ref and missing peeled target data; no additional test fact, so the Linux TRX minimum remains 2,768.

The changes above are newer than CI #45972484561, which targeted the earlier ambiguous-push head `f1986015703297ad9c6c6f71aecc5198660d9868`. A fresh exact-head CI run is required after this journal update. The prior fully-green implementation head `841dd90bbd1e5375558acb20d2b0515c828876d4` predates this publication fix and the least-privilege workflow change.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.


### Follow-up — audit every negative tag-pushed return

- Audited every `new TagEvidence(false, ...)` return in `EnsureImmutableTagAsync`.
- The remaining explicit false outcomes are confined to: a local invalid tag while the successfully parsed remote listing proves no tag exists; failure to create a local tag before any remote push; or a non-zero push followed by a successful, well-formed listing that proves both refs absent or proves a different peeled target.
- Initial remote inspection failure, malformed response, existing remote tag fetch/verification failure, failed/malformed post-push lookup, incomplete tag/peeled ref pair, Candidate-target match with unverified metadata and final verification errors all preserve `tagPushed=true` or the already-established true state.
- An annotated tag whose peeled ref is omitted can still be accepted only when a fetched/locally verified annotated tag object matches the advertised remote tag-object SHA and proves the Candidate target. The tag object SHA is not compared directly with the Candidate commit SHA.
- This closes the last identified path where missing remote evidence could accidentally unlock `FailedBeforeTag`.

CI [#45973251379](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45973251379) targets source/test/workflow head `65471ad201f8cd387a2fc5794ff6bdad4b43b552` and is running. It includes the current code, tests and 2,768-result CI/release gates but predates this journal-only addition. Do not call it green until the run completes; a final exact-head run is still required after this journal update.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Add automated least-privilege Release workflow contract test

**Commits**
- `342e5f3b3d6c9737fd3d970dcce945864fb1014a` — add Python unittest for Release workflow permission scopes and merge_group trigger.
- `555cb8c4b92367197b7560f8170c4095f62da064` — first regex correction for the merge_group assertion.
- `efd558a5b3a3123f3f7b7ab8cd0efc8b7e48905e` — correct the assertion to match actual newline characters and YAML indentation.
- `097d1d6a154d111291603194792478919975c5e3` — run the permission contract test in the Linux CI job.
- `3b0722d8103bfa6bf66385643c6a7192a16845a7` — rerun the same permission contract test in the tag-triggered Release workflow before tag validation.

### Test contract

New `scripts/test_release_workflow_permissions.py` checks:
- Workflow-level permissions are exactly `contents: read`.
- The only job with `packages: write` is `image`.
- `client` has only `contents: write`; `image` has `contents: read, packages: write`; `scan` has `contents: read, packages: read`.
- `tests` and `deploy` do not receive `packages: write`.
- `ci.yml` declares `merge_group: types: [checks_requested]`.

The test uses only Python's standard library and reads the checked-out workflow files; no additional dependencies or secrets are required. It runs once in the authoritative Linux CI job and again in release.yml, so the same permission contract is checked on normal CI and tag publication.

### Validation state

- Static inspection confirms global package write is absent, package write appears only in the image job, package read only in scan, and the CI workflow includes merge_group.
- The existing TRX threshold remains 2,768; this Python test does not add a .NET test result, so no TRX floor change is needed. CI and release.yml TRX guard blocks remain identical.
- The earlier exact-head CI #45962729055 validated source head `841dd90bbd1e5375558acb20d2b0515c828876d4`, before the permission contract test existed.
- CI #45973571011 targets head `d9a0eb767e586a89a4577da112b0e2b0b7239a9f`; it predates the new test script and workflow steps. Its result cannot validate these additions.
- A new exact-head Linux/Windows/macOS CI run is required on the current post-journal PR head. The release workflow's new permission test will additionally run before the first future tag-triggered build; no tag has been created.

No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Trigger full exact-head CI and verify the Python permission contract test passes.
2. Confirm all three platform jobs, all seven test modules, TRX (2,768 minimum), formatting and EF checks remain green.
3. Preserve the external acceptance blockers: org-owned disposable repository for live Merge Queue, queue Merge method=merge, classic v* tag protection and production Environment required reviewers.


## 2026-11-27 — Exact-head CI passed: remote tag safety and least-privilege Release workflow

**CI run:** [#45974881947](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45974881947)  
**Validated implementation SHA:** `500c2e872239c9807920fc94ae67c66095164ab1`  
**Result:** success on Linux, Windows and macOS.

### Validation details

- All three platforms passed Restore, Build, EF pending-model-change validation, all seven test modules and `dotnet format`.
- Linux authoritative lane passed the workflow permission contract test before build/test.
- Linux TRX gate: **2,768 results**, 1 expected platform-conditional skip, 0 failures, 7 modules. Both workspace symlink security gates passed.
- `ci.yml` and `release.yml` retain identical full TRX guard blocks and the same `MIN_TOTAL = 2768`.
- `merge_group: checks_requested` is present in CI; the workflow permission contract test verifies its presence.
- This run includes the latest ReleasePublish ambiguous-push handling, complete remote annotated-tag verification, byte-exact tag identity, FailedAfterTag retry lifecycle coverage and the automated Release workflow permission contract.

### Changes represented by this validated head

- Ambiguous/failed tag push and incomplete remote inspection are fail-closed as potentially published. `tagPushed=false` is only accepted where a successful, well-formed remote listing conclusively proves the Candidate tag is absent or belongs to a different commit.
- Existing/concurrently-created remote annotated tags are fetched to unique temporary refs and checked for exact tag object SHA, peeled commit, type, tag name, Release/Candidate identity and version. CR, trailing whitespace, altered and duplicate identity lines are rejected.
- After a tag might have been published, retry can only reuse the original Candidate/tag/version/endpoint payload. Completion evidence must match the exact queued Publish identity; missing/malformed/mismatched evidence never becomes negative proof.
- Release workflow permissions are constrained and regression-tested: global contents read; image job alone has packages write; scan has packages read; client gets contents write for GitHub Release assets; tests/deploy do not get package write.
- The Linux test floor is kept aligned between regular CI and tag-triggered release workflow.

### Remaining acceptance blockers

1. Live async GitHub Merge Queue acceptance needs a writable org-owned disposable repository. ProjectBeacon is public and owned by a personal account; the connected account currently has no organization memberships or other writable repository. Do not simulate queue scenarios or destructive PR operations against ProjectBeacon.
2. On that eligible test repository, an administrator must set the queue Merge method to `merge` and required CI checks, then verify pending/enqueued, UUID expiry with/without queue entry, queue removal, direct/queued completion, closed-unmerged PR, head/base drift and failed post-merge check.
3. A repository administrator must verify classic tag protection `v*` and required reviewers on Environment `production`; current integration lacks permission to inspect those settings.
4. End-to-end real-project acceptance remains: ReleasePrepare → merge-commit landing → immutable Candidate → Publish/tag → release Actions workflow → exact deployed version/SHA, including failure and retry recovery.

### Status and safety

- PR #230 remains open and unmerged. `main` is not behind the feature branch.
- No production tag or deployment was created. No PR was merged.
- SaaS Control Plane and billing were not touched.
- This journal-only commit changes the PR head; run CI again on the post-journal SHA because the workflow intentionally does not run on `pull_request.synchronize`.

### Next

1. Trigger and verify exact-head Linux/Windows/macOS CI on the post-journal commit.
2. Resolve external merge-queue and publication-protection acceptance blockers when an eligible organization repository/admin is available.
3. Continue user-project UI, localization, RLS and release recovery acceptance without merging or publishing production.


## 2026-11-27 — Preserve normal GitLand merge strategy; scope Release merge constraint

**Commits**
- `00e0bc0fbbcf4d0135ddbcd4e3afa16c24614bca` — remove unconditional merge_method=merge from shared GitHubLandingMergeExecutor.
- `d2f1448ce622ed70f4363f1d0043b55f139e3bd1` — update the shared executor regression test to assert ordinary GitLand omits merge_method and retains merge_action=default.
- `ce6577819e57df8c7907f32f97d8e7dba38874b5` — clarify Release PR instructions and VerifyLanding diagnostics for direct vs queued merge strategy.

### Scope correction

- GitHubLandingMergeExecutor is the shared executor for ordinary task GitLand PRs, not a Release-only executor. Forcing `merge_method=merge` there would have changed the repository's configured/default merge strategy for every ordinary GitHub task PR.
- Removed that global override. Ordinary GitLand again sends the exact source SHA and `merge_action=default`; it leaves the merge method to the repository/queue configuration as before.
- The Release-specific invariant remains independent and fail-closed: the original ReleasePrepare commit must be an ancestor of the target branch, or VerifyLanding refuses to create Candidate. The tag workflow independently requires its original commit and Beacon-Release trailer.
- A Release PR must therefore be landed with a merge commit. If a merge queue is enabled, its administrator-selected Merge method must be `merge`. Without a queue, the repository's direct merge strategy must create a merge commit (or an Owner/Admin must explicitly select that option). Squash/rebase cannot produce a Candidate.
- Important distinction: the current shared GitLand executor does not force a special method only for Release PRs. A dedicated Release-specific merge command would be a separate feature if the merge itself must be initiated by Beacon rather than through repository/approver configuration.

### Validation state

- Exact-head CI #45974881947 passed on implementation SHA `500c2e872239c9807920fc94ae67c66095164ab1` across Linux/Windows/macOS; Linux had 2,768 results, one expected platform skip, zero failures. That green run predates the scope correction above.
- The current code/test changes require a fresh exact-head CI. Earlier queued/journal-only run #45977259934 does not validate these newer commits.
- PR #230 remains open/unmerged. No production tag/deployment. SaaS Control Plane and billing remain out of scope.

### Next

1. Re-run exact-head Linux/Windows/macOS CI after this journal entry.
2. Confirm normal GitLand retains its prior merge policy and Release Verify/tag gates continue to reject squash/rebase.
3. Complete the external live queue and publication-protection acceptance once an eligible organization repository and administrator are available.


## 2026-11-27 — Tighten Release workflow write-permission regression guard

**Test commit:** 4ee8625e999adac5dc0958482725ad9aaa3d8658

- The existing Release workflow permission test already asserted workflow-level contents:read, client contents:write, image packages:write, scan packages:read, and no packages:write on tests/deploy.
- It did not reject a future, unrelated write scope on tests/deploy (for example contents:write or id-token:write).
- Added an exact write-scope matrix assertion: only client may receive contents:write and only image may receive packages:write; tests, scan and deploy must have no write-scoped permissions.
- No application behavior or .NET test count changed; Linux TRX minimum remains 2,768. The standalone permission test runs in both CI and release workflow before build/release validation.
- The prior exact-head CI #45977749031 passed on SHA 3fa2dc36a253ddbccde3dddaa545a3a826201cb1. This new Python regression test is not covered by that run; a new exact-head Linux/Windows/macOS run is required after the journal update.

### Next

1. Re-run exact-head CI on the post-journal branch head; confirm the permission contract test and all three platform jobs pass.
2. Continue static release workflow and UI/RLS acceptance. External merge-queue and production protection settings remain administrator acceptance blockers.
3. PR #230 remains open and unmerged; no production tag or deployment. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Localize Release Prepare/Publish failure summaries

**Implementation commits**
- beeb442df8138e03d320e8eb33f9baa1bbc89488 — English base Release failure resources.
- 4ed9f47e9ff32bd25725dcad7e6ea901aa0e67dc — Russian translations.
- a5aaf49bbf7733df8c4dd0bba27e659c4d8e02f5 — German translations.
- 39093054488f15c4333b0d6a2826ee54cc32e60b — Japanese translations.
- 0efcc485281f15d642447e107c21dc4f7662611e — Chinese translations.
- 14f6a09f57f9a609f5e5cfd13abec65d1742e639 — render localized Prepare/Verify and Publish failure summaries in Roadmap.
- 803e655ad03e4e62ff111e28bbf8aecaf8cee6af — include the new resources in the existing all-cultures localization availability test.

### UI behavior

- Roadmap previously rendered raw Release PreparationError and PublicationError strings as a small warning caption. Those diagnostics come from the workstation/API path and are generally English, even when the UI locale is Russian/German/Japanese/Chinese.
- The UI now shows a translated, explanatory failure summary and a separate diagnostic-details label, while preserving the complete technical text with line breaks and long-line wrapping.
- Publication summary explicitly warns that if the immutable tag was pushed, a retry will reuse the same Candidate/tag.
- Added all three keys to the existing UI notification resource test for en/ru/de/ja/zh. No new test case was added, so the Linux TRX count floor does not change.
- LocalizationParityTests already checks that newly added neutral keys exist in every supported satellite resource; no key was added to the untranslated baseline.

### Validation

- CI #45980768813 was started on SHA 34111f1f2f05ba96e5abd5fe109bc012a3c6b445 and predates these localization changes.
- These Razor/resource/test updates require a fresh exact-head Linux/Windows/macOS CI run. No .NET/application behavior or database schema changed.
- PR #230 stays open and unmerged. No production tag/deployment. SaaS Control Plane and billing remain out of scope.

### Next

1. Finish the localized diagnostic UI and all-cultures test on exact-head CI.
2. Continue release UI/RLS acceptance and validate retry states without creating a production tag.
3. External Merge Queue, Merge method=merge, classic v* tag protection and production Environment reviewers remain acceptance blockers.


## 2026-11-27 — Rehydrate Candidate evidence for Publish and completion

**Implementation commits**
- f764da264843491ad2c8b0c6c9071e0bd0b45926 — add a domain method to reattach an immutable Candidate snapshot to a freshly loaded Release aggregate. The method validates project/Release/Candidate identity, pinned workstation, Candidate revision, version and target branch; it does not change business state or revisions.
- 26ab2c4488b9b0ca5dcdd3d056e0fd4a3ab00a2a — PublishReleaseHandler reattaches the separately loaded Candidate row before BeginPublication.
- d55e45ab3199366a2f6907ae53739c9dea16eb82 — ReleasePublishCompletion reattaches the same persisted Candidate row before CompletePublication.
- 58761cde64797ae238bcd10d93b15bc1882954eb and 4afe9620b22f859802662ce874a636d2934f03c4 — add/strengthen the fresh-DbContext application integration test.
- c5d591a331b2d9ac7c8db02fcbd300c2581977b3 — do not lock version policy merely because a Release has already been Published.
- 4285f6fc5caa1db4a8313f9e5de045b4504dc168 — extend the existing policy test to publish a Release, then successfully change/disable its version policy.

### Critical defect and fix

- ReleaseConfiguration explicitly ignores Release.CandidateSnapshot; the immutable Candidate evidence is stored in ReleaseCandidateSnapshots as a separate row.
- PublishReleaseHandler already loaded that row but did not reattach it before calling Release.BeginPublication. After EF reload, CandidateSnapshot was null, so the domain transition rejected every normal production Publish attempt.
- ReleasePublishCompletion also loaded the immutable Candidate row separately but did not reattach it before Release.CompletePublication, so a successfully tagged/deployed release would be rejected as FailedAfterTag.
- Added explicit, fail-closed aggregate rehydration. It accepts only the exact persisted Candidate whose ProjectId, ReleaseId, CandidateId, pinned device, pre-Candidate release revision, target version and target branch match the reloaded aggregate. It does not mutate the database row or change the aggregate revision.
- Added an integration test that persists a Candidate, opens fresh DbContexts, queues publication, opens another context for workstation completion, and verifies Published status, immutable tag, deployed version and exact deployed SHA. It also proves that a snapshot with a different CandidateId is rejected.

### Completed Release version-policy lock

- SetProjectVersionPolicyHandler previously considered every non-cancelled Release with PreparationStatus != NotStarted active, including already Published releases (whose preparation state remains Candidate).
- The policy lock now excludes both Cancelled and Published Releases. InProgress/ Candidate workflows and non-cancelled failed preparation remain locked as before.
- Extended the existing all-cultures/project version policy test to create a fully Published Release and then successfully change the tag pattern and disable Release Management without changing the official version.

### Validation state

- The prior CI run #45981186013 targeted pre-fix SHA a3ac90d3b2f5bc96ce91d77fa422ed0cb73b9f4e. It does not validate the Candidate rehydration fix, integration test or Published-policy regression.
- A new exact-head Linux/Windows/macOS CI is required after this journal commit. No production tag, deployment or PR merge was performed.
- PR #230 remains open/unmerged. SaaS Control Plane and billing remain out of scope.

### Next

1. Run exact-head CI and resolve any compile, test, EF or formatting issues.
2. Verify the new end-to-end fresh-context Publish/Completion integration test passes.
3. Continue the remaining external Merge Queue and publication protection acceptance only when an eligible org-owned disposable repository and administrator access are available.


### Follow-up — synchronized CI result floor for fresh-context Publish test

- Added one new Application integration fact, `PublishAndCompletion_RehydrateCandidateSnapshotAcrossFreshDbContexts`, covering both queue and completion through fresh DbContexts.
- Raised the authoritative Linux TRX minimum from 2,768 to 2,769 in both `.github/workflows/ci.yml` and `.github/workflows/release.yml`.
- Current thresholds are both 2,769 and their complete TRX guard blocks are byte-for-byte identical.
- Workflow commits: `57bca5d7b4a134b18b681280a203bfceef426066` and `2962247979515cee3d4379d16dc79eb98156e2e8`.
- These workflow commits are newer than the previous journal entry and require exact-head CI.



## 2026-11-27 — Merge latest main locale audit into release branch

**Merge commit:** `b34f72682913cf597cdfe2fc61c024d15d2dd5fa`  
**Parents:** feature `24efb8d55b2f2f29bda46196a27cfcfa9bfb1548`, main `7258d4e20a4cc5ad32f5a7b0994ae400365a4c5a`.

- Main had advanced one commit with the locale audit/completion. Integrated it using a real two-parent merge commit; no force-update.
- Only the five shared Resource files conflicted logically. The merge retained the updated main translations and re-applied the three Release error summary/detail keys in each culture.
- Verified all five files contain `ReleasePreparationFailedSummary`, `ReleasePublicationFailedSummary` and `ReleaseDiagnosticDetails`.
- The branch is now ahead of main and behind by zero commits. No application code or SaaS Control Plane/billing work was changed by this merge.
- The previous in-flight CI #45982946580 targeted pre-merge SHA `24efb8d55b2f2f29bda46196a27cfcfa9bfb1548` and cannot validate the merged head. Exact-head CI is required after this entry.


## 2026-11-27 — Restore Release Management localization and fix publication audit metadata

**Failure investigated:** CI [#45983775039](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45983775039) on SHA `104bde53dbbef9825c1905b11a54126d1e37abff` failed on all three platforms:
- Application: `PublishAndCompletion_RehydrateCandidateSnapshotAcrossFreshDbContexts` failed because `PublishReleaseHandler` emitted audit details with `candidateId`, which was not allowlisted.
- Web: three GitHub merge state tests and the Version Impact render test failed because resource keys had been dropped during the earlier five-culture merge.

### Audit metadata

- Added narrowly validated audit keys for `candidateId`, `candidateSha`, `tagName`, `workstationId` and integer `publicationRevision`.
- GUIDs must parse in canonical D format; Candidate SHA must be 40/64 hex characters; tagName must pass the same safe Git tag validator as VersionPolicy. Unknown/free-form audit keys remain rejected.
- Removed the optional deployment endpoint hostname from the audit payload rather than adding an unnecessary free-form host field.
- Added two Domain audit regression facts: accepted immutable publication metadata and rejection of malformed IDs/SHA/tag/revision values.
- Implementation/test commits: `8176d3e1b413b806cf5415417c65e500f862830e`, `87257716dce632c6767eb51e9c494b6105c42bbf`, `88792dac1d655ceacc5f11fb668856ca4f64a555`.

### Localization merge regression

- The previous journal entry said that only three Release error summary/detail keys needed reapplication. That statement was incomplete: comparison with the feature parent before merge `b34f72682913cf597cdfe2fc61c024d15d2dd5fa` found **105 additional Release Management keys** absent from each of the five current resource files.
- The missing set included all Release/Version Policy/Roadmap labels, GitHub merge pending/enqueued/failed reasons, and Task Version Impact labels.
- Restored the 105 resource entries from feature parent `24efb8d55b2f2f29bda46196a27cfcfa9bfb1548` into current neutral, German, Japanese, Russian and Chinese resources, preserving values already updated on main.
- Each of the five resource files now has exactly 1,345 keys; key-set comparison reports zero missing and zero orphan keys for every supported culture.
- Updated the existing bUnit tests to compare rendered text against the corresponding `IStringLocalizer<Web>` value instead of fixed English phrases.
- Resource commits: `0e26babc440c42155be87a68f98482f353701f36`, `cc4f341caff91114e203e0670682544f6b31f564`, `7b7d136c9d5077218bd8f13cf8823c4a9fd0f9c2`, `5b7e42fe599d7a24f32d830823596e0e37fd6eeb`, `1cbac1855804cc5af4ce0c66362350858ebfa624`.
- Test commits: `e75dcfde85d2b7b81e31cceabea1aa02e86b0369`, `9770c15ea766ee9ca97f85be576e12ff9b4ce7e7`, `41dd8d8b7d4b845f35b8660389db85695a92b1a5`, `f2c6390f7cfaa11e32a955328a54335d6a898873`.

### CI gate

- Added two audit facts; raised the Linux TRX minimum from 2,769 to **2,771** in both `ci.yml` and `release.yml`.
- The full TRX guard blocks are byte-for-byte identical.
- Workflow commits: `c1c85ca85ce7a21c06809ee73771403aa3d25493`, `703cb245430eedd8204c5b6370913675d7475329`.
- Resource key sets and test changes were statically verified through GitHub file contents. No local .NET execution was available in this session; exact-head CI is required to confirm compilation, test behavior, satellite resource assemblies and formatting.

### Next

1. Run full Linux/Windows/macOS CI on the post-journal head; verify the Application publish/completion integration test, all four Web tests, 2,771 Linux TRX floor and formatting.
2. Fix any remaining failures before calling this branch ready.
3. Live Merge Queue acceptance still needs an eligible org-owned disposable repository; queue method=merge, classic `v*` tag protection and `production` reviewers remain administrator verification items.
4. No production tag, deployment or PR merge. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Close post-main locale merge regressions and strengthen resource coverage

**Validation source:** CI [#45983775039](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45983775039), failed on SHA `104bde53dbbef9825c1905b11a54126d1e37abff`.

### Root causes from the failed run

- Application: `PublishAndCompletion_RehydrateCandidateSnapshotAcrossFreshDbContexts` failed because `AuditEvent.ValidateDetails` rejected the new immutable publication metadata `candidateId`.
- Web: the GitHub merge state bUnit tests and Task Version Impact render test failed after the locale merge. The UI tests were importing the wrong `Web` resource type for `IStringLocalizer<Web>`, so the assertions were not using the satellite `ProjectBeacon.Web.Resources.Web` resources.
- The prior locale-merge journal had only recorded the three Release error-summary keys as reapplied. This was incomplete: comparison with feature parent `24efb8d55b2f2f29bda46196a27cfcfa9bfb1548` showed 105 additional Release Management keys missing from each resource file.

### Corrections already on the branch

- `AuditEvent` now has a narrow allowlist for immutable publication metadata: Candidate id/SHA, tag name, workstation id and publication revision. GUID, SHA and safe Git tag values are structurally validated; free-form audit values remain rejected.
- Restored the missing 105 Release Management localization entries across en/ru/de/ja/zh, preserving main's updated translations. All five files now contain exactly 1,345 resource keys with no missing or orphan keys.
- Updated TaskPipelinePanel tests to use the actual `ProjectBeacon.Web.Resources.Web` localizer and compare against rendered localized resources rather than fixed English strings.
- The TaskDetail Version Impact render test now configures an explicit XML version source, matching a real project VersionPolicy.
- Added the GitHub async-merge pending/queued/failed strings and all Task Version Impact labels to the existing all-cultures resource availability test. This extends the existing test without adding a new test case.

### Current exact-head checks

- Latest implementation/journal head: `70ff6d85c35b6a52fdcdc399c4610a49615beb2b`.
- Five culture files: 1,345 keys each; all 15 GitHub Merge/Task Version Impact keys are present in every culture.
- `.github/workflows/ci.yml` and `.github/workflows/release.yml` both require at least 2,771 Linux TRX results; the complete TRX guard blocks are byte-for-byte identical.
- CI still subscribes to GitHub `merge_group: checks_requested`.
- The failed run #45983775039 is pre-fix and cannot validate these corrections. No CI run has yet validated the current exact head; re-run the full Linux/Windows/macOS matrix.

### Next

1. Run exact-head CI on `70ff6d85c35b6a52fdcdc399c4610a49615beb2b`; verify the Publish fresh-DbContext integration test, GitHub merge bUnit tests, Version Impact render test, all-culture resource checks, 2,771 TRX minimum and formatting.
2. Continue static and integration review of Release lifecycle and publication.
3. Live Merge Queue acceptance still needs a writable org-owned disposable repository; verify queue Merge method=merge, classic `v*` tag protection and `production` Environment reviewers with an administrator.
4. Keep PR #230 open until all acceptance blockers are closed. No production tag/deployment; SaaS Control Plane and billing remain out of scope.


### Follow-up — generated resource marker type qualification

- The exact-head build attempt #45986685330 exposed `CS0118`: in the test namespace, short type name `Web` resolved to namespace `ProjectBeacon.Web`, not generated resource marker type.
- Confirmed the marker is the internal generated class `ProjectBeacon.Web.Resources.Web`; `ProjectBeacon.Web.csproj` grants `InternalsVisibleTo` to `ProjectBeacon.Web.Tests`.
- Updated the two bUnit test files to use the fully qualified marker type `global::ProjectBeacon.Web.Resources.Web`.
- Test commits: `142fcc19081caa6135b94556fd649bbe47086726`, `0d72f310cacb92619f4435652cc1f24caad6600a`.
- The run #45987044512 targets the preceding head `ce4f2a4c6de79af9458342d50d68db3fb74b1efe`, so it predates this type-qualification fix. The latest PR head `0d72f310cacb92619f4435652cc1f24caad6600a` requires a new exact-head CI run.


## 2026-11-27 — Sanitize ReleasePublishCompletion audit events

### Defect found during post-fix review

- The first allowlist correction unblocked the queued Publish audit event, but the fresh-context publish integration also creates completion audit events.
- Completion still attempted to put free-form `error` and `workflowRunUrl` values in append-only audit details, plus fields `tagPushed` and `workflowRunId` that were not yet allowlisted. The success path also used non-contract field names `version` and `deployedCommitSha`.
- Left unchanged, the integration could pass the queued publish and then fail when the daemon completion wrote a success/failure audit record.

### Fix

- Failure audit records now contain only `candidateId`, boolean `tagPushed`, a stable machine `resultCode`, and `workflowRunId` only when a positive id exists. Raw error strings remain on the Release publication diagnostic fields and are not copied into the security audit log.
- Successful publication audit records now use the established structured keys: `candidateId`, `tagPushed`, `targetVersion`, `tagName`, `candidateSha`, `deployedSha` and `workflowRunId`. The full Actions URL is omitted from audit details; it remains on the Release publication record.
- Added `tagPushed` and `workflowRunId` to the allowlist. `workflowRunId` must be a positive integer; `deployedSha` receives the same strict 40/64-hex validation as `candidateSha`.
- Extended the existing Domain audit test for accepted/invalid publication metadata.
- Extended the existing fresh-DbContext Application integration test to assert the persisted Published audit record contains exact Candidate/version/tag/deployed SHA/run id and excludes `workflowRunUrl` and free-form `error`.
- No additional test facts were added; CI and release.yml TRX floors remain 2,771.

**Implementation/test commits**
- `0c47493b8c7b1f77ab56d19d25fa263c9b1fa481` — sanitize ReleasePublishCompletion audit fields.
- `7f0f970f5c04ce42e334f6feabde57c5b3aadb34` — validate workflow run id and deployed SHA in audit metadata.
- `32470c633fe1cc4a6d887e5890ba14311b1aae98` — extend audit metadata regression assertions.
- `cb2c1d3fbb809b456720f933b1a59815937813f0` — assert exact sanitized audit evidence in the fresh-DbContext Publish integration test.

### Validation state

- CI #45983775039 is the pre-fix failure on `104bde53dbbef9825c1905b11a54126d1e37abff`; its audit failure was the first missing allowlisted key and did not exercise the later completion audit path.
- Current code and tests are newer than that run. A new exact-head Linux/Windows/macOS CI is required.
- No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.


### Follow-up — structured audit evidence for publication completion

- Completion handling previously attempted to emit free-form error text, workflow URL and completion metadata that did not comply with the append-only audit allowlist.
- Replaced this with a dedicated `PublicationAuditDetails` builder containing only Candidate id, `tagPushed`, a short result code and an optional positive workflow run ID.
- Successful publication audit records exact structured evidence: Candidate id, `tagPushed=true`, `targetVersion`, tag name, Candidate SHA, deployed SHA and workflow run ID. Workflow URL and arbitrary error text are deliberately excluded.
- Extended AuditEvent validation:
  - `candidateId`/`workstationId`: canonical GUID;
  - `candidateSha`/`deployedSha`: 40- or 64-character hex Git SHA;
  - `tagName`: same safe-tag validator used by VersionPolicy;
  - `workflowRunId`: positive integer;
  - `tagPushed`: scalar boolean.
- Added negative coverage for invalid deployed SHA and zero workflow-run ID. The fresh-DbContext Publish/Completion integration test now reads the persisted `project.release.published` AuditEvent and asserts every expected field while asserting that workflow URL and free-form error are absent.
- Implementation commits: `0c47493b8c7b1f77ab56d19d25fa263c9b1fa481`, `7f0f970f5c04ce42e334f6feabde57c5b3aadb34`.
- Test commits: `32470c633fe1cc4a6d887e5890ba14311b1aae98`, `cb2c1d3fbb809b456720f933b1a59815937813f0`. These extend existing facts and do not add additional TRX facts; the synchronized Linux minimum remains 2,771.

### Follow-up — resource marker type build correction

- CI #45987044512 targeted intermediate SHA `ce4f2a4c6de79af9458342d50d68db3fb74b1efe` and the Linux/macOS Build reported `CS0118`: short name `Web` resolved to the namespace rather than the generated resource class.
- The generated marker is internal `ProjectBeacon.Web.Resources.Web`; Web project has `InternalsVisibleTo` for `ProjectBeacon.Web.Tests`.
- Both bUnit files now use the fully qualified marker `global::ProjectBeacon.Web.Resources.Web` in `IStringLocalizer<T>`.
- Test commits: `142fcc19081caa6135b94556fd649bbe47086726`, `0d72f310cacb92619f4435652cc1f24caad6600a`.
- The intermediate run predates those corrections. Current code and the latest audit-completion assertions require a new exact-head cross-platform CI. No green result is claimed for the current head.

### Current state

- All five resource files contain 1,345 keys with matching key sets; zero missing or orphaned keys.
- CI and release workflow TRX minimums remain synchronized at 2,771; the complete guard blocks are identical.
- Exact PR head before this log append: `cb2c1d3fbb809b456720f933b1a59815937813f0`.
- PR #230 remains open, unmerged and mergeable; do not merge until exact-head CI and external acceptance requirements pass.
- Live Merge Queue acceptance still requires an eligible org-owned disposable repository. Queue method=merge, classic `v*` tag protection and `production` Environment reviewers remain administrator verification items.
- No production tag/deployment; SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Scope ReleasePublish audit helper to the correct completion class

**CI diagnosis:** Run [#45987491865](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45987491865) on SHA `16abf8e891f315af63b14d3546da040e2e9bbd18` found a compile error on macOS and Linux:

- `CS0103: PublicationAuditDetails does not exist in the current context` at four ReleasePublishCompletion call sites.
- The helper had been inserted before the first `AddAudit` method in the file, which belonged to `ReleasePrepareCompletion`, not `ReleasePublishCompletion`. The same-file placement made the helper inaccessible to the publisher.

**Fix:**
- Moved `PublicationAuditDetails` into `ReleasePublishCompletion` immediately before that class's own `AddAudit` helper.
- Confirmed all four call sites and the helper declaration are inside the same class; no duplicate/misplaced declaration remains.
- The remaining API/Application/CLI/Infrastructure/Web/Worker test-launch failures on macOS and Linux were downstream of the failed solution Build (the test assemblies were not produced), not separate test assertion results.
- Windows completed Build without failures on the previous head, but this does not validate the corrected helper.

**Correction commit:** `8d343b437ea06096ecedae5bb29854223f3e05ee`.

### Validation

- Run #45987491865 is a known failed pre-fix run on SHA `16abf8e891f315af63b14d3546da040e2e9bbd18`; do not treat it as current validation.
- Current branch contains the helper-scope correction. A new full Linux/Windows/macOS run is required on the post-journal head.
- CI and Release Linux TRX floors remain 2,771; their complete guard blocks remain synchronized.
- No production tag, deployment or PR merge was performed. SaaS Control Plane and billing remain out of scope.

### Next

1. Trigger exact-head CI after this journal update.
2. Verify macOS/Linux/Windows Build and the fresh-context Publish completion audit integration test.
3. Resolve any further test or formatting failures before considering merge readiness.
4. Keep external live Merge Queue and publication-protection acceptance blockers open until an org-owned disposable repository and administrator verification are available.


## 2026-11-27 — Exact-head CI green after publication audit scope fix

**CI run:** [#45988134913](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45988134913)  
**Validated SHA:** `2ad852f6d083004a194466aa98af7c242204d508`  
**Result:** success on Linux, Windows and macOS.

- All three platform jobs passed Build, EF pending-model-change validation, seven test modules and formatting.
- Linux authoritative lane: 2,771 TRX results, one expected platform-conditional skip, zero failures; symlink security gates passed.
- The ReleasePublishCompletion audit helper scope correction and fresh-DbContext Publish audit assertions passed in the Application tests on all three platforms.
- Resource/localization regressions remained closed; Web tests passed on Linux, Windows and macOS.
- This supersedes the earlier failed pre-fix run #45987491865, whose `CS0103` build error was caused by the helper being placed in the wrong completion class.

### Follow-up — enforce audit JSON value types

- A final schema review found that generic scalar validation could still accept `workflowRunId` as a string and `tagPushed` as a string/number, despite these being an integer and boolean contract.
- Added explicit JSON value-kind enforcement: `workflowRunId` must be a positive Int64 JSON number; `tagPushed` must be a JSON boolean.
- Extended the existing Domain audit test with negative cases for string/negative/fractional workflowRunId and string/numeric tagPushed. No new test facts were added.
- Implementation/test commits: `3180d3e248169aad31d630f2f6aa9cd3cf3694d2`, `7d3b1ee17c396bb8f3c22b770d0c44f58cc7be31`.
- CI/release TRX floors remain 2,771 and their full guard blocks remain identical.
- These strict-type changes are newer than successful CI #45988134913 and require a fresh exact-head run.

### Next

1. Run Linux/Windows/macOS exact-head CI for the audit JSON type enforcement and confirm formatting.
2. Keep PR #230 open until external Merge Queue and publication protection acceptance is complete.
3. Live queue tests require a writable org-owned disposable repository; admin must verify queue Merge method=merge, classic `v*` tag protection and `production` Environment reviewers.
4. No production tag, deployment or PR merge. SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Serialize Release milestone assignment with Candidate preparation

### Race found during lifecycle review

- `SetMilestoneReleaseHandler` checked whether the source and target Release accepted milestone changes, then changed `Milestone.ReleaseId` without taking the project mutation lock.
- `PrepareReleaseHandler` builds the immutable Candidate scope while holding `ProjectRuntimeMutationGate.TryLockProjectAsync` in a transaction.
- A concurrent Owner/Admin milestone reassignment could therefore pass the `PreparationStatus == NotStarted` check immediately before Prepare captured its scope, then commit while Prepare was snapshotting. The task/milestone association could differ from the Release's frozen Candidate membership.
- This is a time-of-check/time-of-use race; ordinary revision checks on the Milestone do not serialize it with ReleasePrepare because the two operations mutate different rows.

### Correction

- `SetMilestoneReleaseHandler` now runs through the EF execution strategy and an explicit transaction.
- It acquires the same project-row mutation lock as Prepare/Verify/Publish before reading the Milestone, source Release or target Release.
- Authorization, feature-gate checks, source/target lifecycle validation, assignment, audit and persistence now all happen inside that transaction.
- A missing project fails closed; stale Milestone revisions and concurrent updates return a conflict.
- Added a regression assertion to the existing `PreparedReleaseFreezesItsMilestoneTaskScope` test: detaching a Milestone after preparation begins is rejected and the persisted Release association remains unchanged. This extends an existing test fact, so the Linux TRX floor remains 2,771 and the CI/release guard blocks remain synchronized.

### Implementation and validation

- Implementation: `cbec965caa81831c868af3faaf329072561e1373`.
- Regression test: `7cbec7877c60a08d1dc81c4e42c05b4c5b149292`.
- Current PR head at the time of this journal entry: `7cbec7877c60a08d1dc81c4e42c05b4c5b149292`.
- Exact-head cross-platform CI is required after these changes. Earlier green CI #45988134913 validates only the previous SHA `2ad852f6d083004a194466aa98af7c242204d508`; it does not validate this correction or the strict audit JSON-type change.
- No PR merge, production tag or deployment. SaaS Control Plane and billing remain out of scope.

### Next

1. Confirm Linux, Windows and macOS CI on the current exact PR head; verify the seven-module suite, 2,771 Linux TRX floor, EF model check and formatting.
2. Continue checking release lifecycle operations for mutations that need the same project lock, especially cancellation/deletion paths that can race Prepare.
3. Keep PR #230 open until exact-head CI and external GitHub publication/merge-queue acceptance requirements are complete.


## 2026-11-27 — Final Candidate-scope and repository-operation serialization pass

### Additional write paths closed

The full-path review found more writes outside ordinary task CRUD. These are now serialized with the same project-row lock and transaction as ReleasePrepare:

- Device remote-landing reconciliation now takes the project lock before task/proof checks and holds it through proof persistence and `Task.ClosePipeline`.
- Label deletion takes the project lock, enumerates affected tasks and checks their milestone membership before clearing `Task.LabelId`.
- Project deletion takes the project lock and is rejected when any Release history remains, preventing cascaded deletion of a Candidate's parent and its audit history.
- `EnqueueCommandHandler` now rejects new GitLand commands while any repository operation or pipeline session is active. The check is performed after device, project-membership and runtime authorization, inside the transaction that inserts the command. This closes the queue race in which GitLand could be submitted after ReleasePrepare had passed its preflight.
- Existing GitLand Request/Execute/Revert flows use this central enqueue boundary; no separate queue-side bypass is left in those handlers.

### Follow-up correctness fixes

- `RecordReviewVerdictHandler` detaches the TaskItem before its guarded SQL update. This keeps the guarded ExecuteUpdate as the sole Task state writer and prevents SaveChanges from replaying a stale tracked state.
- `DeleteLabelHandler` explicitly converts the generic coordinator result to the non-generic Result contract.
- The Device remote-landing wrapper was corrected to one execution-strategy/transaction closure.
- The project deletion error text now describes permanent deletion only; no unsupported archive/retirement action is implied.
- The task-update regression request was checked against the current command record signature.

### Regression coverage

- `PreparedReleaseFreezesItsMilestoneTaskScope` now covers reassignment, task edit, checklist insertion and label deletion after preparation begins.
- `PreparedReleaseFreezesMilestoneMetadataDeletionAndLifecycleChanges` covers Milestone update/delete/close/reopen and persisted-state invariance.
- `GitLandExecute_RequiresCurrentServerPinnedPolicyRevision` now also proves that an active `ReleasePrepare` command blocks a new GitLand enqueue, then verifies the existing stale/current policy revision checks after the preparation command is removed.
- One new xUnit Fact was added in this pass; the total Linux TRX floor is therefore 2,772. The CI and release-workflow guard blocks are kept identical.

### Current state

- Branch: `feat/release-management`.
- PR: #230; still open, Ready for review, and unmerged.
- Pre-journal branch head: `6e02692cb58f78884294b31300e03f80a44dd7a2`.
- The earlier green runs validate older commits only. Trigger and verify the full Linux, Windows and macOS CI at the post-journal head before treating the branch as ready to merge.
- Do not create a production tag or deployment from this branch. SaaS Control Plane, billing and unrelated work remain out of scope.


## 2026-11-27 — Resolve intermediate CI failures and close remaining snapshot writes

### Intermediate CI findings on `a6a6d6b`

The cross-platform run completed with failures that were diagnosed from the job logs:

- Four PostgreSQL finish-work concurrency tests observed two successful requests. The project-lock re-read had incorrectly reset the original state/content revision compare-and-swap baseline. The handler now refreshes the tracked entity snapshot after obtaining the lock but preserves the request's original baseline, so a rival commit remains a Conflict and cannot append a second result.
- The concurrent Reviewer-verdict test expected the stage conflict text including terminal punctuation; the handler now emits the exact stable text.
- The tenant-filter allowlist test flagged the new coordinator file. New Task/Step/Milestone resolution now uses normal global query filters; the reviewed cross-tenant bypass list is not expanded for the coordinator.

### Additional Candidate-scope coverage

- Project `Name` is part of the immutable candidate payload. `UpdateProjectHandler` now refuses a name change while a prepared Candidate is still frozen, while continuing to serialize all project metadata writes with the project lock.
- Deleting a task that is used as a dependency by a task inside a frozen Candidate now also checks the referencing task's milestone before deleting dependency rows. This closes the inbound-dependency case where the deleted task itself was outside the Release.
- Existing `PreparedReleaseFreezesItsMilestoneTaskScope` now additionally verifies project rename and inbound dependency-target deletion return Conflict and leave persisted state unchanged.
- Release creation is also serialized with the project lock, matching Release update/status/delete and milestone assignment.

### Validation

- Intermediate CI on `a6a6d6b` confirmed all three platform builds and EF model checks succeeded, but API/Application tests exposed the failures above. No claim of green CI is made for that SHA.
- The fixes are committed on this branch; the next required step is a fresh Linux/Windows/macOS run against the final post-journal SHA.
- The Linux TRX floor remains `2,772` (one additional xUnit Fact was added earlier in this pass); CI and release workflow guard blocks remain identical.
- PR #230 remains open and unmerged. No production tag or deployment. SaaS Control Plane, billing and unrelated branches remain out of scope.

### Current branch head before this journal entry

`3858704f34d79561e2d15e88a76b04c212dc8aa5`


## 2026-11-27 — Preserve FinishWork stale-request CAS and align concurrency assertion

### Further CI diagnostics

The next intermediate run on `2e04b6c` reached all platform test suites. Linux API/Application failures were isolated to:

- FinishWork concurrency: the project lock correctly serialized competing requests, but the first post-lock read replaced the original state/content revision baseline. A stale rival could then be treated as a fresh request and append a second result.
- Reviewer-verdict concurrency: the handler's stable stage-conflict message included its final period, while the test's allowed-message literal omitted it.
- The then-current helper still contained broad tenant-filter bypasses; those lookups have since been changed to normal filtered queries.

### Corrections

- FinishWork now refreshes only the tracked Task snapshot after acquiring the project lock. The compare-and-swap status, state revision and content revision captured before the lock remain unchanged. A rival commit therefore fails the existing CAS and rolls back all content/comment side effects.
- Updated the concurrent ReviewVerdict test to match the exact stable error text, including terminal punctuation.
- Task, Step and Milestone lookups in the shared Candidate mutation coordinator continue to use the normal global tenant filters.
- Added further regression coverage for project-name mutation after Prepare and for deleting a non-Candidate task that is referenced as a dependency by a Candidate task.

### Validation status

- The `2e04b6c` run started before the final FinishWork CAS and ReviewVerdict assertion fixes, so it is not the final validation run.
- The next run must validate the final post-journal SHA on Linux, Windows and macOS, with all seven test modules, the 2,772 TRX floor, EF model check and formatting.
- PR #230 remains open, Ready for review, and unmerged. No production tag or deployment; SaaS Control Plane and billing remain out of scope.

### Current branch head before this journal entry

`ffa15f768eac40dae7b12276ca979a3a0948cb9e`


## 2026-11-27 — Restore milestone authorization semantics and make Windows transcript test deterministic

### CI findings on prior head `8ff31b6`

- Linux API: `P2AuthorizationHttpTests.MilestoneMutations_RequireProjectMembershipAndRespectProjectScopedRoutes` returned 404 instead of the documented 403 for an unauthorized read of an existing milestone.
- Linux, Windows and macOS Application: `IgnoreQueryFiltersAllowlistTests` still expected the old milestone-handler count of 9 after the implementation had been reduced to 3/4 reviewed lookups.
- Windows CLI: `NativeRuntimeDoesNotSummarizeALoneCheckpointAgain` read the transcript as soon as the in-memory turn status became Ended; the final durable write could still be in flight, causing a transient Windows file-sharing IOException.

### Corrections

- `GetMilestoneHandler` uses one explicitly keyed lookup by milestone id and optional route ProjectId before performing project membership authorization. This preserves 403 for an existing resource and 404 for an id outside the route project.
- Project-scoped Milestone mutation handlers now authorize the supplied route ProjectId before the tenant-filtered milestone lookup. Mismatched route project IDs still return 404 after authorization; unauthorized users receive 403 before resource lookup.
- Legacy unscoped Milestone mutation routes resolve only the owning ProjectId of the supplied milestone id, immediately authorize membership, then take the project lock and continue through the tenant-filtered lookup.
- Reviewed allowlist entry for `ProjectBeacon.Application/Milestones/MilestoneHandlers.cs` is now exactly 5, with the rationale describing the three explicit project/milestone lookup paths plus the legacy-id lookup. The remaining mutation queries stay tenant-filtered.
- The Windows transcript test now disposes the runtime before reading the persisted JSON. `DisposeAsync` waits for the active turn task and final persistence, avoiding sleeps/retry loops and making the assertion deterministic.

### Validation

- No new test fact was added; the Linux TRX floor remains 2,772 and CI/release guard blocks remain identical.
- New exact-head Linux/Windows/macOS CI is required after this commit. The failed prior run is superseded only when all jobs on the latest head pass.
- PR #230 stays open and unmerged. No production tag/deployment. SaaS Control Plane and billing remain out of scope.


### Allowlist reconciliation follow-up

- `DeleteProjectHandler` now explicitly checks Release history across the tenant filter before allowing project deletion. The query is restricted by the exact requested `ProjectId`.
- Updated `docs/ignore-query-filters.allowlist` for `ProjectBeacon.Application/Projects/ProjectHandlers.cs` from 11 to 12, with a rationale covering the Release-history guard. This is the final count correction after the Candidate-scope changes.
- The in-flight CI run #46001290569 validates the preceding SHA `75390c3`; its allowlist assertion is expected to reflect the preceding manifest count. Require a fresh run on the latest head for final acceptance.


### Final static reconciliation before the latest CI attempt

- A last repository-wide check against `docs/ignore-query-filters.allowlist` found that the pipeline lifecycle handler had one remaining bypass, not two, after the launch lookup was moved back to tenant-filtered queries. The manifest is now corrected from 2 to 1.
- Counts rechecked on the branch: MilestoneHandlers 5; ProjectHandlers 12; TaskLifecycleHandlers 6; PipelineLifecycleHandlers 1; PipelineControlHandlers 1; PipelineReviewHandlers 1; DeviceRemoteLandingReconciliationHandler 27; WorkstationCommandHandlers 10. Each matches its reviewed manifest entry.
- Run #46001290569 was cancelled after subsequent pushes and is not an acceptance result. Run the full matrix again on the latest branch head.


## 2026-11-27 — Preserve project-scoped Milestone 404/403 contract

### Latest exact-head API finding

The cross-platform run #46001832895 on `40339eb` exposed one remaining mismatch in `P2AuthorizationHttpTests.MilestoneMutations_RequireProjectMembershipAndRespectProjectScopedRoutes`:

- Requesting a milestone id through another project's route while authenticated as an outsider must return 404 (the milestone is not in that route project).
- The coordinator authorized the requested project before checking the milestone/project pair, so it returned 403 before discovering the route mismatch.

### Correction

- For project-scoped Milestone mutations, the coordinator now performs an exact, tightly scoped unfiltered existence lookup on `MilestoneId + route ProjectId`.
- If the pair does not exist, it returns 404 before authorization; this does not reveal whether the id belongs to another project.
- If the pair exists, project membership authorization runs and returns 403 for an outsider.
- After authorization, the handler takes the project lock and reloads the Milestone with normal tenant filters before applying the Candidate-scope guard and mutation.
- Updated the reviewed allowlist for `MilestoneHandlers.cs` from 5 to 6 calls; the new sixth lookup is explicitly constrained by both route identifiers.

### Validation status

- Run #46001832895 correctly identified the mismatch on Linux; Windows and macOS were still running their API suites when the fix was committed.
- The prior run is not accepted as green. A new full Linux/Windows/macOS matrix must validate the corrected route contract and all subsequent suites.
- PR #230 remains open, Ready for review and unmerged. No production tag or deployment; SaaS Control Plane and billing remain out of scope.


## 2026-11-27 — Green exact-head CI and production configuration boundary

### Validation

- Last fully green code head before adding the new workflow-security assertions: 65329d3fc87329e0733cc0d705e5bce9b35e4822.
- GitHub Actions run #46002824395 completed success on Linux, Windows and macOS. All three jobs completed without failed steps.
- PR #230 remains open, Ready for review, mergeable and unmerged. No tag or deployment was created.

### Production settings inspection

- Repository rulesets endpoint returned [].
- Branch protection read returned 403 for the connected integration; GitHub Environment and legacy tag-protection settings are not readable through the current connection. These are recorded as unverified, not assumed absent.
- Static workflow inspection confirms CI has only contents: read and no repository secret expressions. Production SSH references are confined to the deploy job, which is tied to environment production and depends on tests, image, scan and client jobs.
- Added script-level regression checks for CI secret isolation and deployment-secret scoping in scripts/test_release_workflow_permissions.py. Exact-head CI is required after this commit.

### Next

1. Validate the new workflow-security assertions on the latest branch SHA.
2. Keep the first publication blocked until the repository owner verifies release-tag protections and production Environment reviewers/secrets in GitHub Settings.
3. After that external configuration is confirmed, finish the acceptance review of candidate CI vs tag-triggered release workflow; do not create a release tag in this implementation PR.
4. Keep PR #230 open until code checks and release safety prerequisites are complete.


## 2026-11-27 — Preflight GitHub workflow observability before release tag creation

### Finding

- ReleasePublishExecutor previously called EnsureImmutableTagAsync before checking whether GitHub CLI was installed/authenticated.
- A workstation with valid git push credentials but missing GitHub CLI authentication or Actions read access could push the immutable release tag, triggering the production workflow, and only then fail to observe its result.
- This is a fail-unsafe ordering: workflow observability is a required part of publication and must be established before the first possible tag push.

### Correction

- GitHub CLI presence and gh auth status --hostname github.com are now checked before tag creation.
- The executor also runs gh run list --repo <origin> --workflow release.yml and validates the JSON response before it calls EnsureImmutableTagAsync. This proves that the authenticated CLI can read the exact release workflow's run list.
- A failed or timed-out preflight aborts before this command can push a new tag. If a tag already exists from a previous attempt, completion handling remains conservative and treats missing failure evidence as “tag may already exist”.
- Added ReleasePublishExecutorTests coverage for empty-but-valid workflow lists, access failures, timeouts and malformed responses.
- Added script-level CI/deploy secret-scope tests in scripts/test_release_workflow_permissions.py.
- Raised both mirrored Linux TRX floors from 2,772 to 2,773; the CI and release workflow guard blocks remain identical.

### Validation status

- Previous run #46020122611 is for ce88a48, before this preflight change. Its Python workflow-security assertions passed in the Linux lane, but it does not validate the current code.
- New branch head: de3e1306277b332756c57fa0c1d50677b7395c49.
- Run the full Linux/Windows/macOS matrix on this exact head before accepting the preflight change.
- PR #230 remains open and unmerged. No release tag or deployment was created.
- The external production Environment and tag protection settings remain unverified through the connected GitHub integration; see the production safeguards update in the release-management plan. They remain a blocker before the first production publish.


## 2026-11-27 — Make publication preflight prove the workflow is active and preserve pre-tag evidence

### Additional preflight hardening

- A valid empty gh run list response alone does not distinguish an active workflow with no prior runs from a disabled/missing workflow.
- Before a tag can be pushed, the publisher now calls gh workflow list --all --repo <origin> --json id,name,path,state and requires exactly one .github/workflows/release.yml with a positive workflow id and state == active.
- It then calls gh run list --workflow release.yml and validates a well-formed response. Both definition and run-read checks must succeed before EnsureImmutableTagAsync.
- Unit coverage in the existing workflow-preflight fact now includes active, disabled, missing, malformed, forbidden and timed-out cases.

### Preserve exact publication failure state

- FailBeforeTag creates a payload-bound publication result with explicit tagPushed=false.
- Every known failure after the immutable command payload has been validated but before tag creation uses this result, including missing runtime root, unavailable Git/remote, missing/unauthenticated GitHub CLI, missing/disabled workflow, Actions read denial and invalid Candidate commit/tree.
- The executor also tracks whether EnsureImmutableTagAsync has been entered. Unexpected exceptions before that boundary return explicit negative evidence; exceptions after entering the ambiguous tag operation remain conservative and do not claim that no tag was pushed.
- Added a regression fact proving that the structured pre-tag failure JSON contains the exact Release identity and a Boolean false tagPushed value. The server completion still ORs this with any prior known tag-pushed state, so a failed retry cannot erase an earlier publication attempt.

### Validation state

- Branch head at this journal entry: 17e629bc14c18db77d3599c161fa01120b319523.
- The CI run #46021125789 is for an earlier SHA (516b779) and cannot validate the active-workflow definition check or the explicit pre-tag failure result.
- Linux TRX floor remains 2,773; CI and release workflow guard blocks are identical. The disabled/missing workflow cases extend an existing Fact, so no additional TRX result was introduced.
- Run a fresh full Linux/Windows/macOS matrix on the post-journal SHA before accepting this work.
- PR #230 remains open and unmerged. No release tag or production deployment was created. External tag protection and Environment production settings remain unverified and block first publish.


## 2026-11-27 — Exact-head matrix green; remote merge authority verified

### CI validation

- Exact branch head: `0c1d749610fc42a391e85a85430e27cdc2bf0804`.
- GitHub Actions run #46022091769 completed `success` on Linux, Windows and macOS.
- All seven suites passed on each platform: API, Application, CLI, Domain, Infrastructure, Web and Worker.
- Build, EF model check and formatting passed on all platforms. Linux also passed the release-workflow permission test, release-manifest tag-policy test and TRX guard (`MIN_TOTAL = 2773`).
- No compiler, test, model-diff, TRX or formatting failures remain on this SHA.

### S4 — server-authorized GitHub merge path (reviewed against source and tests)

- `ExecuteLandingHandler` accepts a manual request only from a human user-bound actor, or an automatic request only from the trusted Worker. It rechecks current Git policy revision, latest Approve verdict, exact successful Review proof, task/source SHA, review base, pinned workstation and candidate evidence before queueing trusted GitLand.
- The pinned workstation's `GitHubLandingMergeExecutor` rechecks the clean task worktree and exact source SHA, same fetch/push GitHub origin, pinned remote base, unique matching PR, PR head/base/state, mergeability, CLEAN merge state, APPROVED review decision and all required checks.
- It calls GitHub's async merge endpoint with the exact PR head SHA and `merge_action=default`. It does not request `bypass_rules`, admin merge, force, or a separate merge strategy.
- A `pending` or `enqueued` response is persisted as an accepted asynchronous request, not reported as landed. AutoSync then polls the exact PR/request; reconciliation requires MERGED state and verifies merge commit, remote base ancestry, tree and post-merge checks.
- `GitHubLandingMergeExecutorTests` assert the exact-head async request arguments and explicitly reject bypass/admin/force options; response and merge-tree tests cover queued, merged, failed and malformed states.
- This resolves the stale journal item that said the agent still required an external manual merge. GitHub remains the authority for remote merge execution; the workstation is the validated executor and evidence reporter.

### Remaining external blocker

- Repository rulesets were empty on the inspected endpoint; branch protection read returned 403 for the connected integration. Legacy tag protection and Environment `production` settings remain unverified, not presumed absent.
- Before first production publish, the repository owner must verify tag update/delete protections, production Environment required reviewers/self-review policy and deployment refs, and confirm the four production SSH secrets are scoped to that Environment.
- PR #230 remains open, Ready for review and unmerged. No production tag or deployment was created.
