# Command delivery semantics

Workstation commands flow through the queue exactly once. This page fixes the
delivery contract for every `WorkstationCommandKind` so that reapers and retry
mechanisms know, per kind, whether re-execution is safe. The stale-command
recovery rule that consumes this contract lives in `CommandRecovery`
and is documented in `command-recovery.md`.

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
  server (the completion handler is guarded by the command's terminal status
  and the generation check, see `command-recovery.md`). The daemon retries the
  completion POST on transport failures and 5xx only, with bounded
  exponential backoff, and never re-executes the command body.
- **Reconciliation** — a separate loop (heartbeat / `ReconcileDesired`) re-applies
  a desired state that a lost command failed to bring about. This is how a lost
  `ReloadProxy` or `ApplyOpencode` eventually lands. It is not a retry of a
  specific queue row.

`IsReconcileBacked` marks kinds that have a reconciliation backstop. It is
orthogonal to retry: a reconcile-backed kind is still re-executed only if it is
also idempotent. Today every reconcile-backed kind is idempotent, so the
backstop is safe; do not assume otherwise in future code.

## Classification

All 24 `WorkstationCommandKind` values, complete. The executable source of truth is
`WorkstationCommandCatalog`; tests assert the exhaustive partition.

| Kind | Property | Retryable | ReconcileBacked | Rationale |
|---|---|---|---|---|
| `Probe` | Idempotent | yes | no | Read-only workstation/environment probe. |
| `ListDir` | Idempotent | yes | no | Read-only one-level directory listing. |
| `Install` | Idempotent | yes | no | Supported package installation converges when repeated. |
| `InitProject` | Idempotent | yes | no | Project bootstrap operations are guarded merges/no-ops. |
| `RetiredAgentConfigApply` | Idempotent (disabled) | yes | yes | Retired descriptor; cannot be newly enqueued. |
| `ScanGguf` | Idempotent | yes | no | Read-only local model discovery. |
| `ReloadProxy` | Idempotent | yes | yes | Rebuilds proxy configuration from desired state. |
| `UnloadProxy` | Idempotent | yes | no | Unloading an already-unloaded model is a no-op. |
| `SwapModel` | Idempotent | yes | no | Sets the resident model; same model converges to same state. |
| `SaveWorkstation` | Idempotent | yes | yes | Persists the same desired workstation configuration. |
| `ChatEnsureSession` | **AtMostOnce** | **no** | no | Creates a new external session; replay can orphan another session. |
| `ChatPrompt` | **AtMostOnce** | **no** | no | An LLM call; replay creates a duplicate assistant turn. |
| `ChatAbort` | Idempotent | yes | no | Aborting an already-aborted session is a no-op. |
| `ChatSwitchAgent` | **AtMostOnce** | **no** | no | Control-lane operation with external side effects; never re-deliver after ambiguous completion. |
| `ChatReplyForm` | **AtMostOnce** | **no** | no | Submits one answer to a pending external form; duplicate submission is not guaranteed safe. |
| `RetiredProviderSync` | Idempotent (disabled) | yes | yes | Retired descriptor; cannot be newly enqueued. |
| `RunEvalTurn` | **AtMostOnce** | **no** | no | Duplicate LLM turn would double-count evaluation results. |
| `ReconcileDesired` | Idempotent | yes | yes (self) | Reconciles workstation state from desired state; next heartbeat can trigger it again. |
| `RunReviewCheck` | **AtMostOnce** | **no** | no | Runs one trusted check for a review run; replay can create another run. |
| `RunPipelineSession` | **AtMostOnce** | **no** | no | Starts one Actor/Planner/Review session; replay can duplicate work and tool side effects. |
| `CheckClientUpdate` | Idempotent | yes | no | Read-only release metadata check. |
| `UpdateClient` | **AtMostOnce** | **no** | no | Downloads/verifies/installs a client release; ambiguous interruption is not re-executed. |
| `GitSync` | Idempotent | yes | no | Repeating status/fetch/fast-forward converges; successful fast-forward is subsequently UpToDate. |
| `ApplyProposalPatch` | Idempotent | yes | no | Each file is checked against exact base/result hashes; already-applied sections are skipped and only remaining base-state sections are applied. A third state is a conflict, never success. |

Counts: **16 Idempotent**, **8 AtMostOnce** (`ChatEnsureSession`, `ChatPrompt`,
`ChatSwitchAgent`, `ChatReplyForm`, `RunEvalTurn`, `RunReviewCheck`,
`RunPipelineSession`, `UpdateClient`), **5 ReconcileBacked**
(`RetiredAgentConfigApply`, `ReloadProxy`, `SaveWorkstation`,
`RetiredProviderSync`, `ReconcileDesired`). Disabled retired descriptors remain in
the exhaustive enum/catalog partition but cannot be newly enqueued.

## Current protection for AtMostOnce

The seven at-most-once kinds are protected from duplicate execution by the
combination of the following. **All of these must be preserved.**

1. **Atomic claim (H4.1).** The claim is a guarded
   `UPDATE … WHERE Status='Pending'`; exactly one daemon wins the row, so the
   body executes once per queued command.
2. **Reaper that never re-delivers at-most-once (H4.4 / P1.4).** A command
   left in `Running` is recovered by the `CommandRecovery` rule (worker,
   every 60 s), but only through the stale predicate: the lease is expired
   AND either the claiming process is gone (device revoked, offline, or a
   different process instance) or the lease expired long ago — the long-stale
   tier, 30 minutes past expiry, which also covers a live daemon with a
   wedged loop or a lost acknowledgement (no legitimate execution can still
   be in flight at that point, every kind is hard-capped client-side).
   At-most-once kinds are **failed, never re-queued**; idempotent kinds are
   re-queued at most once, then failed. A stuck at-most-once command is
   therefore lost, not re-delivered — the intended at-most-once-with-loss
   behaviour — and no recovery path ever writes `Succeeded`. See
   `command-recovery.md` for the full rule, the lease contract, and the
   generation protocol.
3. **Guarded completion.** `CompleteCommandHandler` applies the row's terminal
   transition; a completion on an already-terminal row is rejected, and a
   completion carrying a stale generation is rejected, so neither a duplicate
   nor a late completion POST can double-apply effects.

### Invariant (binding for every future reaper, retry, or re-queue mechanism)

> A reaper, retry, or re-queue mechanism **must not** reset a `Running`
> at-most-once command to `Pending`. Before re-queueing any `Running` row,
> consult `CommandDelivery.IsAtMostOnce(kind)` and skip (or fail) those rows.
> Consult `IsIdempotent` / `IsReconcileBacked` only to decide *how* to recover
> an idempotent lost command, never to re-execute an at-most-once one.
> `CommandRecovery.Decide` is the executable form of this invariant.

### GitSync

`GitSync` is a project-runtime, idempotent command. It is human-initiated by a project Owner/Admin or system administrator and is queued only to a workstation explicitly advertising the `git-sync-v1` heartbeat capability, so older daemons are never sent an unknown enum value. Its payload is restricted to `projectId` and `mode` (`Status` or `PullFastForward`). Re-delivery is safe: fetching the same tracked upstream converges on the same remote-tracking ref, and a repeated fast-forward after success returns `UpToDate`. The normal command lease applies; recovery may requeue a stale GitSync once under the existing idempotent recovery rule. This does not make arbitrary Git operations idempotent or permitted.

`Status` refreshes the current branch's tracked upstream and reports ahead/behind counts and worktree state. `PullFastForward` additionally refuses dirty, detached, unconfigured, diverged, or actively used repositories without applying a merge.

## Cross-references

- `command-recovery.md` — the stale-command recovery rule, lease contract,
  and generation protocol that consume this classification.
- `mcp-host.md` — the command kind table and the workstation client contract.
- `ProjectBeacon-Hardening-Roadmap.md` — H4.1 (atomic claim), H4.2 (this
  contract), H4.3 (versioned command envelope), H4.4 (recovery, P1.4).
- `CommandDelivery` (Application/Devices) and `CommandDeliveryTests`
  (Application.Tests) — the executable form of this contract.
