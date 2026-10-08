# Task pipeline (current contract)

The implementation log is historical: `archive/docs/task-pipeline-local-agents-log.md`. Do not implement from it. Architecture: `ProjectBeacon-design-doc-v3.md`.

## Phases

Each role is a new session. Prompt text is `SessionPrompts`.

1. **Planning.** The planner sees the task title, description, and project name. It creates subtasks with instructions and optional `AllowedMcpTools` / `AllowedPaths`. It does not implement the task.
2. **Executing.** The actor sees only that subtask's instructions, plus the allowed-tool and allowed-path lists when they are non-empty. It does not see the planner transcript or the parent task. It reports a diff reference and a summary.
3. **Review.** The reviewer sees the task text and, for each subtask, the instructions, diff ref, and summary. It does not see the other transcripts. The verdict is approve, or reopen one subtask with a fix note.
4. **Close.** Approve moves the task to approved; the user confirms close. Force-close is a separate path and records that review was skipped.

## Spawn

`ManualSessionSpawner` is the `ISessionSpawner` registered for pipeline sessions. `IAgentRuntime` / `OpenCodeAgentRuntime` run on `beacon client` for workstation chat. They are not the pipeline spawner.

## Scope fields

`AllowedMcpTools` and `AllowedPaths` are persisted on the subtask and copied into the actor prompt. Nothing in the MCP host or the spawner turns those lists into harness permission denies.

## What does not run the pipeline

`ProjectBeacon.Worker` only expires sessions, API tokens, password-reset tokens, and invites. It does not launch planner, actor, or review sessions.

## Not the same as cold diff review

Pipeline review is the product's reviewer session and verdict. `Skills/cold-diff-review.md` is an isolated reading of a git diff before push. One does not substitute for the other.
