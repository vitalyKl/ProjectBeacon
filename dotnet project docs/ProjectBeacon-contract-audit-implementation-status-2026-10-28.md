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
2. **Model provider/runtime split.** `ManualSessionSpawner` still constructs a llama-swap launch spec for each backend type, while the backend enum includes API-compatible providers. Do not enable real non-llama process spawning until provider-specific launch/execution contracts and validation are implemented.
3. **Use `IModelBackend` consistently.** `ClientLlamaSwap` still uses concrete `LlamaServerBackend` operations for implementation-specific supervision, VRAM and port details. Extract a lifecycle/metrics contract only after deciding which metrics and supervision capabilities all implementations must provide.
4. **Authorization parity tests.** For sensitive operations, test API and direct Application invocation and assert identical actor/capability/resource scope requirements.
5. **Error code migration.** Legacy failures that call `Failure(string)` now receive the default validation classification. Audit important handlers and assign explicit `ErrorKind`/codes; ensure MCP and UI display codes consistently without leaking internals.
6. **Persisted phase semantics.** Add an explicit phase role/purpose field plus migration and UI editor only if custom task kinds must support arbitrary phase names and pipeline roles. Current fix avoids positional inference and safely leaves a role unresolved when the semantic phase is absent.
7. **MCP legacy migration.** Move persisted custom local MCP commands from a single command string to executable plus argument array. The compatibility adapter cannot recover original shell quoting that was never stored.
8. **Cross-process chat events.** Add a shared transport only if Web readers and writers can run in different processes or instances.
9. **Desired-state generalization.** Promote the existing revision model into a reusable reconciler only when a second/third managed resource needs the same lifecycle. Do not abstract based on a hypothetical.
10. **Storage-neutral persistence.** Extract bounded persistence ports only when an independently testable/deployable module needs them; avoid a repository layer solely to hide EF.

## Verification state

The synchronized branch is being validated by GitHub Actions. Check the PR's latest commit and all required platform jobs before merging; a green result on an earlier commit is not sufficient. The latest validation run is linked from the PR description.
