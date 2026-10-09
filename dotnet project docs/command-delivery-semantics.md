# Command delivery semantics

Workstation commands flow through the queue exactly once. This page fixes the
delivery contract for every `WorkstationCommandKind` so that a future reaper or
retry mechanism knows, per kind, whether re-execution is safe.

The code-level source of truth is `CommandDelivery`
(`ProjectBeacon.Application/Devices/CommandDelivery.cs`). If you change the
classification here, change the helper and its tests
(`ProjectBeacon.Application.Tests/CommandDeliveryTests.cs`) in the same commit.

## The four properties

| Property | Meaning | Authorizes re-execution? |
|---|---|---|
| **AtMostOnce** | Re-execution produces a harmful duplicate (a second LLM turn, an orphaned session, a doubled review run). | **No.** Must never be re-delivered. |
| **Idempotent** | Re-execution converges to the same state; the command reads, or writes a value that depends only on inputs. | Yes, re-execution is safe. |
| **Retryable** | A *transient* failure (network drop on the completion POST, process exit) may be retried. Derived: an idempotent kind is retryable. | Only idempotent kinds, and only for acknowledgement/transport failures, not for a real execution failure. |
| **ReconcileBacked** | A reconciliation loop re-drives the command if it is lost, independent of the queue. | **No.** Reconciliation is a *loss* recovery path, not a retry. It does not authorize re-executing an already-completed command. |

**Execution retry vs acknowledgement retry vs reconciliation.** These are three
different mechanisms and must not be conflated:

- **Execution retry** — re-run the command body after a *failed execution*.
  Allowed only for idempotent kinds, and only when the failure is known to be
  benign. The daemon does not do this today; there is no execution retry.
- **Acknowledgement retry** — re-send the *completion* POST after the command
  body already succeeded but the acknowledgement was lost (network drop, the
  daemon died after `Succeed()` was applied locally but before the HTTP 200).
  Idempotent only in the sense that a duplicate completion is a no-op on the
  server (the completion handler is guarded by the command's terminal status).
  The daemon does not retry the completion POST today.
- **Reconciliation** — a separate loop (heartbeat / `ReconcileDesired`) re-applies
  a desired state that a lost command failed to bring about. This is how a lost
  `ReloadProxy` or `ApplyOpencode` eventually lands. It is not a retry of a
  specific queue row.

`IsReconcileBacked` marks kinds that have a reconciliation backstop. It is
orthogonal to retry: a reconcile-backed kind is still re-executed only if it is
also idempotent. Today every reconcile-backed kind is idempotent, so the
backstop is safe; do not assume otherwise in future code.

## Classification

All 17 `WorkstationCommandKind` values, complete.

| Kind | Property | Retryable | ReconcileBacked | Rationale |
|---|---|---|---|---|
| `Probe` | Idempotent | yes | no | Read-only: detect binaries. |
| `ListDir` | Idempotent | yes | no | Read-only: one-level directory listing. |
| `Install` | Idempotent | yes | no | Winget is re-runnable; an already-installed package is a no-op. A partially failed install is safe to re-run. |
| `InitProject` | Idempotent | yes | no | Every step is a no-op or a merge: `CreateDirectory` (no-op if present), `git init` (guarded by `!Directory.Exists`), `.gitignore` (set-based merge), `opencode.json` (key/value merge), `local.json` (overwrite). A partially failed init is safe to re-run. |
| `ApplyOpencode` | Idempotent | yes | yes | Key/value merge of OpenCode config; deterministic from project state. Re-applied by `ReconcileDesired` if lost. |
| `ScanGguf` | Idempotent | yes | no | Read-only: list `*.gguf`. |
| `ReloadProxy` | Idempotent | yes | yes | Rewrites llama-swap config from desired state; same state → same result. Re-applied by `ReconcileDesired`. |
| `UnloadProxy` | Idempotent | yes | no | Unloading an already-unloaded proxy is a no-op. |
| `SwapModel` | Idempotent | yes | no | Sets the resident model; same model → same state. **Not enqueued through `WorkstationCommand`** — handled client-side inside `ReloadProxy` execution. Kept in the enum for completeness. |
| `SaveWorkstation` | Idempotent | yes | yes | Writes `workstation.json`; same payload → same file. Re-applied by `ReconcileDesired`. |
| `ChatEnsureSession` | **AtMostOnce** | **no** | no | Always `POST /session`, which **creates a new OpenCode session every run** — there is no dedup by title. The "ensure" refers to ensuring the OpenCode *process* is running (`TickAsync`), not ensuring a specific session exists. Re-execution orphans a session. |
| `ChatPrompt` | **AtMostOnce** | **no** | no | An LLM call. A duplicate run produces a second, duplicate assistant turn. |
| `ChatAbort` | Idempotent | yes | no | Aborting an already-aborted session is a no-op. |
| `ConfigureOpenCode` | Idempotent | yes | yes | Applies `/v1/devices/me/opencode-connections` and restarts OpenCode; deterministic from the connection rows. Re-applied by `ReconcileDesired`. |
| `RunEvalTurn` | **AtMostOnce** | **no** | no | An LLM call. A duplicate run corrupts the eval scores by double-counting the turn. |
| `ReconcileDesired` | Idempotent | yes | yes (self) | Applies the device's desired state. It is its own backstop; a lost reconcile is re-triggered by the next heartbeat. |
| `RunReviewCheck` | **AtMostOnce** | **no** | no | Runs `checkCommand` for a `reviewRunId`. A duplicate run produces a second review run. |

Counts: **13 Idempotent**, **4 AtMostOnce** (`ChatEnsureSession`, `ChatPrompt`,
`RunEvalTurn`, `RunReviewCheck`), **5 ReconcileBacked** (`ApplyOpencode`,
`ReloadProxy`, `SaveWorkstation`, `ConfigureOpenCode`, `ReconcileDesired`).

## Current protection for AtMostOnce

The four at-most-once kinds are protected from duplicate execution by the
combination of the following. **All of these must be preserved.**

1. **Atomic claim (H4.1).** The claim is a guarded
   `UPDATE … WHERE Status='Pending'`; exactly one daemon wins the row, so the
   body executes once per queued command.
2. **No reaper.** A command left in `Running` (its daemon died before
   completing) is never reset to `Pending`. There is no timeout reaper today,
   so a stuck at-most-once command is lost, not re-delivered. That is the
   intended at-most-once-with-loss behaviour.
3. **Guarded completion.** `CompleteCommandHandler` applies the row's terminal
   transition; a completion on an already-terminal row is rejected, so a
   duplicate completion POST cannot double-apply effects.

### Invariant for future work

> A reaper, retry, or re-queue mechanism **must not** reset a `Running`
> at-most-once command to `Pending`. Before re-queueing any `Running` row,
> consult `CommandDelivery.IsAtMostOnce(kind)` and skip (or fail) those rows.
> Consult `IsIdempotent` / `IsReconcileBacked` only to decide *how* to recover
> an idempotent lost command, never to re-execute an at-most-once one.

## Cross-references

- `mcp-host.md` — the command kind table and the workstation client contract.
- `ProjectBeacon-Hardening-Roadmap.md` — H4.1 (atomic claim), H4.2 (this
  contract), H4.3 (versioned command envelope).
- `CommandDelivery` (Application/Devices) and `CommandDeliveryTests`
  (Application.Tests) — the executable form of this contract.
