# Refactoring & Design-Pattern Review

_Cold, read-only review of the ProjectBeacon .NET 9 monorepo for refactoring and design-pattern opportunities._

**Scope:** full `ProjectBeacon.sln` (14 projects), excluding `bin/`, `obj/`, migrations, `*.Designer.cs`, `*.g.cs`, generated code.

**Method:** size scan → signal greps (switches, JSON parsing, process execution, DI) → deep reads of top candidates → rule-of-three, SRP, and design-pattern checks. Findings are ranked by value/effort; each cites evidence.

---

## Findings (ranked)

### 1. `McpApiTools` tool registration is triplicated — use a data-driven registry

**Type:** duplication + design pattern (table-driven dispatch). **Effort M · Risk low–med.**

Every MCP tool is registered in three separate string-listed places, plus a handler:

- `Names` set — `ProjectBeacon.Cli/Mcp/McpApiTools.cs:16-33`
- `Definitions()` `yield return Tool(...)` — `McpApiTools.cs:39-163`
- `InvokeAsync` switch arms — `McpApiTools.cs:271-330`
- ~50 uniform handlers, all
  `private static async Task<McpToolText> X(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)`
  — `McpApiTools.cs:338-908`

Adding or renaming one tool means editing three string lists in sync; a miss yields silent inconsistency (advertised but undispatched, or in `Names` with no schema).

**Plan:** introduce

```csharp
record McpTool(
    string Name,
    Func<JsonObject> Schema,
    Func<JsonObject?, BeaconApiClient, McpEnvironment, CancellationToken, Task<McpToolText>> Handler);
```

Build one `static readonly McpTool[]` table; derive `Names`, `Definitions()`, and the dispatch loop from it. `proxy_reload`/`proxy_unload` (which skip a handler — `McpApiTools.cs:327-328`) become inline `Handler` lambdas.

**Verify:** `dotnet test ProjectBeacon.Cli.Tests --filter "McpApiTools|McpStdioServer"`.

---

### 2. Process-execution boilerplate duplicated ~10× — extract one runner

**Type:** duplication (rule-of-three exceeded). **Effort M · Risk med (behavior-preserving).**

The same `ProcessStartInfo → Process.Start → ReadToEndAsync(out+err) → WaitForExit(timeout) → Kill(tree)` sequence is repeated at:

- `Cli/Client/WorkstationActions.cs:281` (`GitPorcelain`), `:313` (`GitHead`), `:610` (`Which`), `:670` (`Run`), `:697` (`RunCaptured`) — 5 in one file
- `Cli/Client/EvalCheck.cs:22-24`, `ClientLlamaSwap.cs:190`, `ClientOpenCodeServe.cs:194`, `NvidiaSmiVramChecker.cs:90-97`, `LlamaServerBackend.cs:116`
- `Infrastructure/LlamaSwap/LlamaSwapSupervisor.cs:248`, `Application/CodeIndex/CodeIndex.cs:565-582`, `Web/Startup/DockerPostgresHelper.cs:103`

A partial helper already exists — `Cli/Client/ProcessControl.cs:43` (`WaitForExitAsync`) — but is not used uniformly.

**Plan:** one `ProcessRunner.RunAsync(ProcessStartInfo, TimeSpan timeout, CancellationToken)` returning `(int ExitCode, string StdOut, string StdErr, bool TimedOut)`; fold the 5 `WorkstationActions` helpers onto it first, then `EvalCheck` / `NvidiaSmiVramChecker` / `CodeIndex`.

**Verify:** `dotnet test` + `dotnet format --verify-no-changes`.

---

### 3. `WorkstationActions` is a god static class — decompose by concern

**Type:** SRP / decomposition. **Effort M · Risk low (all `static`).**

`Cli/Client/WorkstationActions.cs` (803 lines) bundles 14+ unrelated operations: `ProbeJson`, `ListDir`, `RunEvalTurnAsync`, `RunReviewCheck`, `ScanGguf`, `InitProject`, `ApplyOpenCodeConnections`, `ApplyOpencode`, `SaveWorkstation`, `Install`, `MergeGitignore`, `ResolveBeaconPath`, `ReplaceBeaconCommand`, `Which` — mixing process exec, opencode-JSON writing, eval orchestration, filesystem ops, and the installer. It already nests a second static class `OpencodeConfig` (`:726`).

**Plan:** split into focused static classes:

- `ProcessRunner` (see finding #2)
- `OpencodeConfig` (promote; owns `ApplyOpenCodeConnections` / `ApplyOpencode` / `ReplaceBeaconCommand` / `InitProject` config writes)
- `Eval` (eval/review turn)
- keep a thin `WorkstationActions` facade only if callers rely on the name

**Tests:** `WorkstationActionsTests`, `OpenCodeConnectionApplyTests`, `EvalCheckTests`.

---

### 4. `BeaconDbContext.OnModelCreating` is a 600+-line single method

**Type:** long method / standard EF pattern. **Effort L (mechanical) · Risk low.**

`Infrastructure/Data/BeaconDbContext.cs:70+` configures ~50 entities inline in one `OnModelCreating` (file is 709 lines). EF's idiomatic split is one `IEntityTypeConfiguration<T>` per entity, applied via `modelBuilder.ApplyConfigurationsFromAssembly(...)`. This puts each entity's schema next to its code and improves navigability.

**Plan:** extract per-entity `IEntityTypeConfiguration<T>` classes under `Data/Configurations/`.

**Verify:** regenerate a throwaway migration and confirm the model snapshot is unchanged.

---

### 5. JSON field-parsing helpers duplicated in device handlers

**Type:** minor duplication. **Effort S · Risk low.**

`Application/Devices/DeviceHandlers.cs` repeats near-identical `ReadInt`/`ReadNullableInt` and `ReadGuid`/`ReadEvalRunId` accessors; `Application/Devices/DeviceLlamaSwapProxy.cs:109-122` parses the same payload shape again.

**Plan:** a small shared `DevicePayload` reader (static) used by both. The handler-per-class CQRS layout itself is correct and should stay.

---

### 6. `CodeIndex` repeats the read-a-file guard

**Type:** minor duplication. **Effort S · Risk low.**

`Application/CodeIndex/CodeIndex.cs` re-implements the size-check → binary-check → `ReadAllText` → try/catch guard in `GetSignatures`, `SearchFile`, `HashRange`, and `MakeEntry`.

**Plan:** a private `TryReadTextFile(path, maxBytes, out text)` helper. The class is otherwise well-factored (one concern per public method), so this is the only change worth making there.

---

## Deliberately not flagged

- **`DeviceHandlers.cs` handler-per-class split** — matches the codebase's CQRS convention; keep as-is.
- **`CodeIndex.cs` overall** — cohesive; only the file-guard (finding #6).
- **`McpApiTools` handler bodies** — the `TryProject → build JsonObject → SendAsync` shape is fine; the fix is the registration table (finding #1), not the handlers.

---

## Suggested execution order

`1 → 2 → 3 → 4 → 5, 6`. Findings 1 and 2 are independent; 3 reuses 2's `ProcessRunner`. Each step is a standalone, testable commit.

After each step:

```
dotnet build && dotnet test && dotnet format --verify-no-changes
```
