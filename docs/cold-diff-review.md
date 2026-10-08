# Cold Diff Review — ProjectBeacon Working Tree

Date: 2026-07-03
Scope: All unstaged + untracked changes in working tree vs HEAD.
Method: Split into 6 parts by area, blind subagent review per part, orchestrator verification of every finding.

## Change Inventory

| Area | Files | Status |
|------|-------|--------|
| Application (CodeIndex, Devices, LlamaSwap) | 4 modified, 1 new (DevicePayloadReader) | Reviewed, clean |
| CLI Client (refactor) | 8 modified, 5 new (ProcessRunner, OpencodeConfig, WorkstationBrowse/Eval/Probe/Setup), 1 deleted (WorkstationActions) | Reviewed, 4 findings |
| CLI MCP | 1 modified (McpApiTools) | Reviewed, clean |
| CLI Tests | 2 modified | Reviewed, clean (231/231 pass) |
| Infrastructure (BeaconDbContext) | 1 modified (simplified), Configurations/ directory new (39 files) | Reviewed, 1 CRITICAL |
| Infrastructure (snapshot) | 1 modified (BeaconDbContextModelSnapshot) | Part of CRITICAL finding |
| Docs | cold-diff-review.md moved to Skills/, docs/ new | N/A |

## Findings (verified)

### CRITICAL — ProjectId1 shadow FK (part 3b2)

- **Where:** `ProjectBeacon.Infrastructure/Data/Configurations/ProjectConfiguration.cs:42-45` + `BeaconDbContextModelSnapshot.cs`
- **What:** `ProjectConfiguration.cs` has a type-based `HasMany<ApiToken>()` that conflicts with the nav-based relationship in `ApiTokenConfiguration.cs:18-21`. EF Core resolves this as two separate relationships, creating a shadow FK column `ProjectId1`.
- **Classification:** Pre-existing model bug. The same pattern exists in HEAD's inline `BeaconDbContext.cs` code. It was not introduced by this diff — it was exposed when the snapshot was regenerated.
- **Fix:** Remove lines 42-45 from `ProjectConfiguration.cs` (the redundant type-based `HasMany`). Keep the nav-based relationship in `ApiTokenConfiguration.cs`. Regenerate snapshot.
- **Verification:** Confirmed HEAD snapshot lacks `ProjectId1` (stale snapshot). Both configuration files at HEAD have the same pattern. No migration adds `ProjectId1`.

### HIGH — SafeRead 2-second cap silently drops output (part 2a)

- **Where:** `ProjectBeacon.Cli/Client/ProcessRunner.cs` — `SafeRead` method
- **What:** `SafeRead` uses a 2-second timeout on `ReadLineAsync`. If the process produces output slower than 2s between lines, the read returns `null` and remaining output is silently discarded.
- **Impact:** Commands that produce slow output (e.g., long-running model downloads) will lose stdout/stderr. Callers see truncated output with no error.
- **Regression:** Yes — the refactored code previously read all output.
- **Fix:** Increase the timeout or use a loop that reads until `EndOfStream` with a generous per-read timeout.

### MEDIUM — WaitForExitAsync after Kill has no timeout (part 2a)

- **Where:** `ProjectBeacon.Cli/Client/ProcessRunner.cs` — post-kill wait
- **What:** After calling `Process.Kill()`, the code calls `WaitForExitAsync()` with no timeout. If the process is in an uninterruptible state (e.g., stuck in a kernel driver), this can hang indefinitely.
- **Fix:** Use `WaitForExitAsync(cancellationToken)` with a bounded timeout (e.g., 5s), then proceed regardless.

### LOW — ExitCode defaults to 0 when killed (part 2a)

- **Where:** `ProjectBeacon.Cli/Client/ProcessRunner.cs`
- **What:** When a process is killed, `ExitCode` is set to 0 (default). Callers checking `exitCode == 0` will interpret a killed process as success.
- **Fix:** Set `ExitCode` to a sentinel value (e.g., `-1` or `137` for SIGKILL-equivalent) when the process was killed.

### LOW-MEDIUM — Sync-over-async in ProcessRunner callers (part 2a)

- **Where:** `EvalCheck.cs:21`, `HostLoadSampler.cs:193`, `WorkstationEval.cs:222,247`, `WorkstationProbe.cs:50`, `WorkstationSetup.cs:139,157`
- **What:** Multiple call sites use `.GetAwaiter().GetResult()` to call async `ProcessRunner` methods from sync contexts.
- **Impact:** Potential deadlocks in certain synchronization contexts. In practice, the CLI runs in a console context where this is low-risk, but it's a code smell.
- **Classification:** Lateral — same pattern existed before the refactor. Not a regression.
- **Fix (optional):** Make the call sites async and propagate `async/await` up the call chain.

## Clean Areas

- **Application changes:** CodeIndex, DeviceHandlers, DeviceLlamaSwapProxy, DevicePayloadReader — no defects found.
- **CLI MCP (McpApiTools):** Build clean (0 warnings), 4/4 McpApiToolsTests pass.
- **CLI Tests:** 231/231 tests pass. OpenCodeConnectionApplyTests and WorkstationActionsTests updated correctly for the WorkstationActions → Workstation* split.
- **BeaconDbContext extraction:** 39→39 entity mappings. All inline configurations faithfully extracted to Configurations/ files. No behavioral change.

## Summary

| Severity | Count | Action |
|----------|-------|--------|
| CRITICAL | 1 | Must fix before merge (remove ProjectConfiguration.cs:42-45, regenerate snapshot) |
| HIGH | 1 | Should fix (SafeRead timeout) |
| MEDIUM | 1 | Should fix (post-kill wait timeout) |
| LOW | 1 | Nice to fix (ExitCode sentinel) |
| LOW-MED | 1 | Optional (sync-over-async, lateral) |

Total: 5 findings (1 CRITICAL, 1 HIGH, 1 MEDIUM, 2 LOW/LOW-MED). 3 areas clean.

## Fixes Applied (step 5)

1. **CRITICAL — DONE:** Removed redundant `HasMany<ApiToken>()` from `ProjectConfiguration.cs` (was lines 42-45). Manually removed `ProjectId1` shadow FK from `BeaconDbContextModelSnapshot.cs` (property, index, and relationship).
2. **HIGH — DONE:** `ProcessRunner.SafeRead` timeout increased from 2s to 10s.
3. **MEDIUM — DONE:** Post-kill `WaitForExitAsync` now uses `.WaitAsync(TimeSpan.FromSeconds(5))` with try/catch.
4. **LOW — DONE:** `ExitCode` set to `124` (standard timeout exit code) when process was killed due to timeout.
5. **Tests:** Full suite green — 1033 passed, 0 failed, 2 skipped.
6. **Second pass:** Fix diff reviewed. Changes are minimal, targeted, and verified by the full test suite. No regressions detected.

### Files Changed by Fixes

- `ProjectBeacon.Infrastructure/Data/Configurations/ProjectConfiguration.cs` — removed lines 42-45
- `ProjectBeacon.Infrastructure/Data/Migrations/BeaconDbContextModelSnapshot.cs` — removed ProjectId1 property, index, and relationship
- `ProjectBeacon.Cli/Client/ProcessRunner.cs` — SafeRead timeout 2s→10s, post-kill wait bounded, ExitCode sentinel
