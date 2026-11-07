# Task pipeline (current contract)

The implementation log is historical: `archive/docs/task-pipeline-local-agents-log.md`. Do not implement from it. Architecture: `ProjectBeacon-design-doc-v3.md`.

## Phases

Each role is a new session. Prompt text is `SessionPrompts`.

1. **Planning.** The planner sees the task title, description, and project name. It creates subtasks with instructions and optional `AllowedMcpTools` / `AllowedPaths`. It does not implement the task.
2. **Executing.** The actor sees only that subtask's ID and instructions, plus the allowed-tool and allowed-path lists when they are non-empty. It does not see the planner transcript or the parent task. It reports a diff reference and a summary.
3. **Review.** The reviewer sees the task text and, for each subtask, the instructions, diff ref, and summary. It does not see the other transcripts. The verdict is approve, or reopen one subtask with a fix note.
4. **Close.** Approve moves the task to approved; the user confirms close. Force-close is a separate path and records that review was skipped.

## Execution

Pipeline execution uses the Native workstation runtime only. A project must have an online attached workstation, and the relevant pipeline role must resolve to an existing model backend (the semantic task phase when phases are configured, otherwise the project role binding). There is no runtime selector; legacy `agentRuntime` values are ignored and old `opencodeDataDir` transcript paths are read only as a migration alias.

`ManualSessionSpawner` is the `ISessionSpawner` registered for pipeline sessions. It resolves the role model and prepares the launch spec; it does not spawn a process. `StartPipeline` creates the planner session and the task page immediately launches it. The workstation command `RunPipelineSession` is then claimed and executed by the daemon's NativeAgentRuntime. The same command path is used for actor and review sessions.

`LaunchSessionHandler` must reject a session if no online workstation executor or model is available. It must never change `Ready` to `Active` without enqueuing a real workstation command. NativeAgentRuntime is the only supported chat and pipeline runtime.

The task pipeline query exposes server-computed preflight state. The UI disables Start/Launch actions when the required workstation or role model is missing and displays the reason. Legacy workstation settings are migrated to the Native runtime.

Progress snapshots are posted by the daemon while an agent turn runs. They update `PipelineSession.ActivityJson` and `LastActivityAt`; a missing recent signal is displayed as stale rather than as active work. A command that is still Pending is shown as queued, not as an agent actively generating. Command completion persists `FailureReason`. A failed Actor command also marks its still-InProgress subtask Failed, so review and retry logic cannot be blocked by an orphaned status. Retrying a failed session creates a new session using the current role binding and preserves the previous attempt for diagnosis.

## Actor queue and timeouts

The Planner creates subtasks only; it does not launch Actors. When the Planner command completes (including a failure after partial planning), the server starts the oldest Pending subtask. After each Actor command completes, the server starts the next Pending subtask. Only one Actor session per task may be queued or running at a time. Queue advancement is guarded by a task-row update and the Pending-to-InProgress subtask transition, so duplicate completion callbacks or concurrent dispatchers converge on the same open session. Review remains manual after all subtasks are Done or Failed.

The workstation daemon processes one command at a time. The model `Concurrent` setting controls llama-swap residency groups; it does not opt a task pipeline into parallel Actor execution. Actor sessions for a task are serialized independently of that setting.

Pipeline turns use a 75-minute server lease. Workstation settings expose a 15–60 minute absolute limit (default 60) and a no-progress timeout (default 15 minutes). Transcript, tool-call, and form changes reset the no-progress timer; periodic polling and elapsed-duration counters do not. The absolute limit remains the final safety cap.

## Scope fields

`AllowedMcpTools` and `AllowedPaths` are persisted on the subtask and copied into the actor prompt. The assigned subtask ID is also included in the prompt and bound from the trusted `BEACON_SUBTASK_ID` environment for actor lifecycle calls. If the model omits `subtaskId` when reporting success or failure, the MCP host supplies the assigned ID; an explicitly supplied different ID is rejected. These lifecycle tools remain limited to the actor's own in-progress subtask.

## What does not run the pipeline

`ProjectBeacon.Worker` only expires sessions, API tokens, password-reset tokens, and invites. It does not launch planner, actor, or review sessions. The workstation daemon executes `RunPipelineSession` commands; the API only prepares, queues, and tracks those commands.

## Not the same as cold diff review

Pipeline review is the product's reviewer session and verdict. `Skills/cold-diff-review.md` is an isolated reading of a git diff before push. One does not substitute for the other.
