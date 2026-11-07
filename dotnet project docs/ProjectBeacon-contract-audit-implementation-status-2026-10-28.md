# Contract audit — implementation status

**Branch:** `refactor/unified-application-contracts-2026-10-28`  
**Created:** 2026-10-28  
**Updated:** 2026-10-29 after synchronizing with current `main`

This document tracks the implementation follow-up to `ProjectBeacon-contracts-and-unified-services-audit-2026-10-28.md`. It deliberately distinguishes implemented code changes from architectural decisions that need a later, runtime-validated migration.

## Implemented in this branch

| Audit item | Change | Status |
|---|---|---|
| A1 — shared operation definition | Added code-first `OperationCatalog`, descriptors, stable IDs for workstation commands, version/context/target/surface/retry/security metadata, duplicate-ID and completeness validation | Implemented foundation |
| A2 — scattered workstation command context rules | `WorkstationCommandCatalog` is now the single scope inventory used by `CommandSandbox`; each command declares ID, version, summary, payload contract, security boundary and retry semantics | Implemented foundation |
| A3 — concrete handler coupling | Added typed CQRS dispatcher and switched task CRUD/list/get endpoints plus `DeviceLlamaSwapProxy` to contract-based dispatch | Partial; model runtime remains |
| A4 — CQRS dispatch boundary | Added `ICommandDispatcher`/`IQueryDispatcher`, handler registration by interface and duplicate-handler detection | Implemented for current Application assembly |
| A5 — MCP configuration catalog | Added typed local/remote MCP server descriptors and tests for argument boundaries/URL validation; retained a compatibility adapter for existing string configuration | Partial; legacy persisted callers still need migration |
| A6 — stable error contract | Added stable error codes to `Result` and `Result<T>`; API Problem Details include a `code` extension | Foundation implemented; MCP/UI mapping remains |
| A7 — EF-shaped persistence port | Explicitly retained the EF-shaped `IBeaconDb` boundary. Narrower ports are deferred until a bounded module actually needs storage independence | Deliberate decision |
| B1 — phase role inferred from ordering | Pipeline role selection no longer falls back to array position; actor phases resolve only an active phase or the explicit `do` phase | Implemented for current semantic keys; explicit persisted phase-role metadata remains a future schema change |
| B2 — desired-state reconciliation | Command catalog now declares reconciliation semantics; existing revision/lease logic preserved | Metadata foundation only; no universal multi-resource reconciler introduced |
| B3 — authorization parity | Operation metadata states that discovery is not permission and server-side policy remains authoritative; no route gates or RLS removed | Needs dedicated policy-parity test suite |
| B4 — weakly typed MCP configuration | Added `LocalMcpServer` and `RemoteMcpServer`, argument-list preservation and absolute HTTP(S) validation | New typed API implemented; old string input remains compatibility-only |
| B5 — compile-time extension registration | Retained code-first composition and documented that plugin/hot-load registration is not a current requirement | Deliberate decision |
| B6 — process-local chat signal | Retained process-local signal for current single-process topology; cross-process transport is a scale-out requirement, not a current defect | Deliberate boundary |
| B7 — worker scope docs | README now documents stale workstation-command recovery alongside expiry/cleanup jobs | Implemented |

## Invariants preserved

- The durable workstation command queue, version, attempt count, lease and recovery behavior are unchanged.
- The command catalog is metadata, not an authorization grant.
- Existing API route requirements and tenant/RLS behavior remain the enforcement boundary.
- Unknown workstation command kinds fail closed because catalog initialization verifies complete enum coverage.
- Catalog coverage includes the `ChatSwitchAgent` and `ChatReplyForm` kinds added to `main` after the audit branch was created.
- Operation IDs are code-first. The catalog does not enable arbitrary code execution or database-defined handlers.
- `IBeaconDb` remains EF-shaped; no generic repository rewrite was introduced.

## Remaining work before calling the audit fully closed

1. **Validate the exact merge candidate in CI.** Require successful build, migration consistency, tests and formatting on Windows, Linux and macOS for the latest PR head. Results from an older SHA do not validate a newer merge candidate.
2. **Provider runtime implementation remains intentionally gated.** `ManualSessionSpawner` now emits a llama-swap launch spec only for `LlamaCpp`; `FreeToken` and `OpenAiCompatible` retain provider/model identity and have no process launch spec. Do not activate real remote-provider pipeline spawning until a provider-specific execution contract and runtime adapter exist.
3. **Llama process supervision remains specialized.** `ClientLlamaSwap` stores and operates on `IModelBackend` for lifecycle, endpoint and memory metrics. Creation and LlamaServer-specific supervision remain concrete because the current in-process swapper is explicitly a llama-server runner; do not invent generic supervision/VRAM semantics for unrelated providers.
4. **Authorization parity — implemented for model role bindings.** Bind/unbind commands now carry `ActorContext`; Application handlers enforce Project Administer. The HTTP routes require a human actor and use the same handler policy. Direct Application and HTTP denial tests cover a non-admin project actor. Continue extending this pattern when another sensitive direct-call path is found.
5. **Stable error propagation — improved.** Model backend handlers classify missing resources and binding conflicts explicitly. MCP translates RFC Problem Details `code` and `detail` into its tool error text. Remaining legacy `Failure(string)` call sites still receive the default validation classification and should be migrated when touched; do not mass-rewrite unrelated handlers without a behavior inventory.
6. **Persisted phase semantics — safely deferred.** Runtime role resolution no longer infers meaning from array position. A schema role/purpose field and editor are still needed only if custom task kinds are expected to bind arbitrary phase names to pipeline roles. Current behavior leaves missing semantic phases unresolved.
7. **MCP argument boundaries — implemented for persisted project settings.** `McpServerConfiguration` stores executable and argument array separately; save validation and the settings UI preserve argument boundaries. The obsolete `CustomMcpServer` adapter remains only for legacy callers and explicitly cannot reconstruct shell quoting that was never persisted.
8. **Cross-process chat events — deferred by topology.** The current Web readers and writers share one process. Add a shared transport only when they can run in separate processes or instances.
9. **Desired-state generalization — deferred by scope.** The existing revision model and command catalog cover current workstation and project configuration resources. Generalize only when a second/third managed resource needs the same lifecycle.
10. **Storage-neutral persistence — deferred by architecture.** Keep the EF-shaped `IBeaconDb` until an independently testable/deployable module requires a bounded storage-neutral port; no generic repository layer is justified.

## Verification state

CI is a merge gate for the latest PR head. Before merging, require build, migration consistency, tests and formatting to pass on Windows, Linux and macOS for the same latest commit. A green result on an older SHA is not sufficient; use the current checks linked from the PR.
