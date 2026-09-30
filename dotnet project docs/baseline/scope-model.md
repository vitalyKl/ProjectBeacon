# Phase 0 — Scope Model

Frozen baseline. Date: 2026-09-30.

## Scope Hierarchy

```text
User (account owner)
│
├── Account / Security
│   ├── Profile (name, email, password)
│   ├── Sessions (JWT tokens)
│   └── Invitations (sent / received)
│
├── Agents & Models                          ← USER SCOPE (confirmed)
│   ├── Model backends (FreeToken, LlamaCpp, OpenAiCompatible)
│   ├── Pipeline role bindings (planner / actor / review → model)
│   ├── Task kinds / templates
│   ├── MCP defaults
│   └── OpenCode connections
│
├── Workstations / Daemons                   ← USER-OWNED RUNTIME SCOPE
│   ├── Device enrollment (`bcd_` tokens)
│   ├── Capabilities (installed tools, local model runtimes)
│   ├── Command queue (long-poll)
│   ├── Heartbeat / probe status
│   └── llama-swap proxy state
│
└── Projects (membership-based)
    ├── Project configuration (name, description, root path)
    │
    ├── Project ↔ Workstation binding
    │   └── ProjectRuntime (ProjectId, DeviceId, LocalRoot)
    │
    ├── Tasks (board / backlog)
    │   ├── Status: Todo → InProgress → Done
    │   ├── Priority: Low / Medium / High / Critical
    │   ├── Type: Feature / Bug / Improvement / Task
    │   ├── Labels (area tags with optional path prefixes)
    │   ├── Milestones (roadmap phases)
    │   ├── Checklist steps
    │   ├── Dependencies
    │   └── Comments / activity
    │
    ├── Pipeline (per task)
    │   ├── Subtasks (actor sessions)
    │   ├── Review stage
    │   ├── Verdicts (approve / reopen_subtask)
    │   └── Force-close (admin)
    │
    ├── Context
    │   ├── Sections (nodes: Project / Repo / Path / Task scope)
    │   ├── Constraints (Must / MustNot / Security / Compliance)
    │   ├── Compile (token-budgeted brief)
    │   └── Import / Export (AGENTS.md)
    │
    ├── Decisions
    │   ├── Proposed → Accepted → Deprecated / Superseded
    │   └── Decision register
    │
    ├── Milestones (roadmap)
    │   ├── Open → Closed
    │   └── Order / description
    │
    ├── Reports
    │   ├── Board snapshot reports
    │   └── Context cost reports
    │
    ├── API Tokens
    │   └── `bcn_` tokens with capabilities (admin / read / write)
    │
    ├── Members
    │   └── User ↔ Project membership (added / removed by owner)
    │
    └── Chat
        ├── Sessions (per project)
        ├── Prompts (SSE streaming)
        └── Tool calls / parts
```

## Scope Confirmation: Agents are User-Level

**Decision (master roadmap §2, confirmed in Phase 0):**

- Agents and their base configuration are **user-level**, not project-level.
- One user can have multiple projects and reuse the same agent/model configuration through their local Beacon daemon/workstation.
- `/agents` remains as a compatibility redirect to `/settings`.
- Settings explicitly separates **global user configuration** (agents, models, MCP) from **contextual workstation/project operations** (runtime binding, device commands).
- Agent models and agent templates belong to the user account; applying a template still writes that project's role bindings.

## Scope Isolation Rules

| Scope | Owner | Access Control | API Token Access |
|---|---|---|---|
| User | Account owner | Self only | N/A (user-level) |
| Agents & Models | User account | Self only | No (user-level, not project-scoped) |
| Workstations | User account | Self only | No (user-level) |
| Project | Project owner / members | Membership check | Yes (project-scoped `bcn_` tokens) |
| Task | Project member | Project membership | Yes (inherits project scope) |
| Pipeline | Project member / agent | Project membership + delegation | Yes (delegated actor) |

## Delegated Identity (Agent / Workstation Path)

- `ActorId` in `FinishWork`, `ForceClosePipeline`, and pipeline subtask operations is the **delegated identity** path.
- These are used by agents/workstations acting on behalf of a user. The actor is the agent/device, not the human.
- Human/Web/API path: actor must come from the authenticated principal (JWT or API token), never from the request body.
- **P0 §1.5:** Client-supplied `ActorId`, `CreatedByUserId`, `UserId` in request bodies must be removed for human-facing endpoints. For agent/workstation endpoints, the delegation must be explicitly authorized.

## Tenant Isolation

- `TenantScope.EnterUnscoped()` only for bootstrap, migrations, and tests.
- Tenant query filters are fail-closed: null `FilterProjectId`/`FilterOrgId` returns no rows.
- `Guid.Empty` matches no tenants.
- `DaemonDevice` is user-owned and not tenant-filtered.
- `ProjectRuntime` is `(ProjectId, DeviceId, LocalRoot)` — a project has no single `RootPath`.
