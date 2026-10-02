# ProjectBeacon — Endpoint Classification (H1.3)

> **H1.3/H1.4 (Hardening Roadmap).** Per-route classification of every `/v1` endpoint: who may call it, which token capability unlocks it, and which mechanism enforces it. This is the **target + enforced state** — the capability wall H1.4 installs. The H1.1 matrix (`ProjectBeacon-authorization-matrix.md`) remains the *current-behavior* record; do not edit it here.

---

## 1. Actor classes

| Class | Meaning |
|---|---|
| **Public** | `AllowAnonymous` — no credential. Rate-limited where marked. |
| **Human only** | JWT user session. `bcn_` tokens get **403 even with the `Admin` capability** (`RequireHumanActor`). |
| **Human + ApiToken** | JWT user, or `bcn_` token with the listed capability. The `Admin` capability flag unlocks any capability-gated route. |
| **Device only** | `bcd_` workstation token. Humans and project tokens get **403** (`RequireDeviceActor`). |

Capability flags (`ApiTokenCapability`, `long`, `[Flags]`):

| Flag | Value |
|---|---|
| `TaskRead` | 1 |
| `TaskWrite` | 2 |
| `SessionDrive` | 4 (reserved; not referenced by any HTTP route) |
| `ContextRead` | 8 |
| `Admin` | 16 (bypasses `RequireCapability` only — never `RequireHumanActor`/`RequireDeviceActor`) |

## 2. Enforcement mechanisms

| Mechanism | Where | Behavior |
|---|---|---|
| `RequireCapability(caps)` | `ApiTokenAuthMiddleware` route filter | JWT passes; `bcn_` token passes iff `caps.HasFlag(Admin) || caps.HasFlag(required)`; device → 403; missing → 403 |
| `RequireHumanActor()` | `ApiTokenAuthMiddleware` route filter | JWT passes; any `bcn_`/`bcd_` bearer → 403 (closes the creator-token hole: tokens carry `CreatedByUserId` and would otherwise look human) |
| `RequireDeviceActor()` | `DeviceActorBoundary` metadata + boundary middleware | `bcd_` device passes; human/token → 403 |
| Handler guard | e.g. `actor.UserId is null → 401` | Second layer inside capability-gated handlers that need a concrete user (creator identity) |

**Fail-closed guarantee:** a capability-0 token reaches only Public routes; every other route returns 403 at the filter before handler execution. No route in `ProjectBeacon.API` carries bare `RequireAuthorization()` without one of the three filters.

**REVIEW REQUIRED (deferred to H2):** routes marked `REVIEW: creator identity` pass the capability wall, but the handler executes as the token creator's `UserId` (`ActorContextFactory` sets `NameIdentifier` from `CreatedByUserId`). The wall is enforced; the identity semantics are the H2 fix.

## 3. Classification tables

Status: `ENFORCED` — gate present and covered by `CapabilityWallHttpTests` / route scan. `REVIEW: creator identity` — H2 item.

### 3.1 Auth (`AuthEndpoints.cs`) — H1.1 §4.1

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/auth/bootstrap` | Public | — | AllowAnonymous + rate limit | global | ENFORCED |
| POST | `/v1/auth/recover-admin` | Public | — | AllowAnonymous + rate limit | global | ENFORCED |
| POST | `/v1/auth/login` | Public | — | AllowAnonymous + rate limit | — | ENFORCED |
| POST | `/v1/auth/register` | Public | — | AllowAnonymous + rate limit | — | ENFORCED |
| POST | `/v1/auth/forgot-password` | Public | — | AllowAnonymous + rate limit | — | ENFORCED |
| POST | `/v1/auth/reset-password` | Public | — | AllowAnonymous + rate limit | reset token | ENFORCED |
| GET | `/v1/auth/options` | Public | — | AllowAnonymous | — | ENFORCED |
| GET | `/v1/invites/{token}` | Public | — | AllowAnonymous + rate limit | invite | ENFORCED |
| POST | `/v1/auth/change-password` | Human only | — | RequireHumanActor + rate limit | self | ENFORCED |
| POST | `/v1/auth/logout` | Human only | — | RequireHumanActor + rate limit | self | ENFORCED |
| GET | `/v1/auth/me` | Human only | — | RequireHumanActor | self | ENFORCED |
| POST | `/v1/invites/{token}/accept` | Human only | — | RequireHumanActor | invite | ENFORCED |

### 3.2 Version (`VersionEndpoints.cs`) — H1.1 §4.2

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/version` | Public | — | AllowAnonymous | — | ENFORCED |

### 3.3 Context / brief (`ContextEndpoints.cs`) — H1.1 §4.6

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/projects/{p}/context/nodes` | Human + ApiToken | ContextRead | RequireCapability | project | ENFORCED |
| POST | `/v1/projects/{p}/context/nodes` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/context/nodes/{nodeId}` | Human + ApiToken | ContextRead | RequireCapability | project | ENFORCED |
| DELETE | `/v1/projects/{p}/context/nodes/{nodeId}` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/context/import` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/context/export/agents-md` | Human + ApiToken | ContextRead | RequireCapability | project | ENFORCED |
| POST | `/v1/projects/{p}/context/compile` | Human + ApiToken | ContextRead | RequireCapability | project | ENFORCED |
| GET | `/v1/projects/{p}/constraints` | Human + ApiToken | ContextRead | RequireCapability | project | ENFORCED |
| POST | `/v1/projects/{p}/constraints` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/constraints/{id}/activate` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/constraints/{id}/reject` | Human only | — | RequireHumanActor | project | ENFORCED |

### 3.4 Tasks (`TaskEndpoints.cs`) — H1.1 §4.4

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/projects/{p}/tasks` | Human + ApiToken | TaskRead | RequireCapability | project | ENFORCED |
| GET | `/v1/projects/{p}/tasks/{taskId}` | Human + ApiToken | TaskRead | RequireCapability | project | ENFORCED |
| GET | `/v1/tasks/{taskId}` | Human + ApiToken | TaskRead | RequireCapability | task | ENFORCED |
| GET | `/v1/tasks/{taskId}/steps` | Human + ApiToken | TaskRead | RequireCapability | task | ENFORCED |
| POST | `/v1/projects/{p}/tasks` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PUT | `/v1/projects/{p}/tasks/{taskId}` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PUT | `/v1/tasks/{taskId}` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| DELETE | `/v1/projects/{p}/tasks/{taskId}` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| DELETE | `/v1/tasks/{taskId}` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PATCH | `/v1/projects/{p}/tasks/{taskId}/status` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PATCH | `/v1/tasks/{taskId}/status` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PATCH | `/v1/projects/{p}/tasks/{taskId}/substage` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PATCH | `/v1/tasks/{taskId}/substage` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PATCH | `/v1/projects/{p}/tasks/{taskId}/claim` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PATCH | `/v1/tasks/{taskId}/claim` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/projects/{p}/tasks/{taskId}/comments` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| POST | `/v1/tasks/{taskId}/comments` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PUT | `/v1/projects/{p}/tasks/{taskId}/dependencies` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| PUT | `/v1/tasks/{taskId}/dependencies` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PATCH | `/v1/projects/{p}/tasks/{taskId}/review-notes` | Human + ApiToken | TaskWrite | RequireCapability | project | ENFORCED |
| POST | `/v1/tasks/{taskId}/steps` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| PATCH | `/v1/steps/{stepId}` | Human + ApiToken | TaskWrite | RequireCapability | step | ENFORCED |
| DELETE | `/v1/steps/{stepId}` | Human + ApiToken | TaskWrite | RequireCapability | step | ENFORCED |

### 3.5 Pipeline (`PipelineEndpoints.cs`) — H1.1 §4.5

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/tasks/{taskId}/pipeline` | Human + ApiToken | TaskRead | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/start` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/subtasks` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/subtasks/{subtaskId}/session` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/sessions/{sessionId}/launch` | Human + ApiToken | TaskWrite | RequireCapability | session | ENFORCED |
| POST | `/v1/tasks/{taskId}/subtasks/{subtaskId}/result` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/subtasks/{subtaskId}/fail` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/review/start` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/verdict` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/review/check` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/approve` | Human + ApiToken | TaskWrite | RequireCapability | task | ENFORCED |
| POST | `/v1/tasks/{taskId}/pipeline/force-close` | Human + ApiToken | Admin | RequireCapability | task | ENFORCED |

### 3.6 Model backends (`ModelEndpoints.cs`) — H1.1 §4.13

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/models` | Human + ApiToken | TaskRead | RequireCapability | user | REVIEW: creator identity (H2) |
| GET | `/v1/models/proxy/status` | Human + ApiToken | TaskRead | RequireCapability | device proxy | ENFORCED |
| POST | `/v1/models` | Human + ApiToken | TaskWrite | RequireCapability | user | REVIEW: creator identity (H2) |
| DELETE | `/v1/models/{id}` | Human + ApiToken | TaskWrite | RequireCapability | user | REVIEW: creator identity (H2) |
| POST | `/v1/models/bind` | Human + ApiToken | TaskWrite | RequireCapability | user | REVIEW: creator identity (H2) |
| DELETE | `/v1/models/bind/{role}` | Human + ApiToken | TaskWrite | RequireCapability | user | REVIEW: creator identity (H2) |
| POST | `/v1/models/proxy/reload` | Human + ApiToken | TaskWrite | RequireCapability | device proxy | ENFORCED |
| POST | `/v1/models/proxy/unload` | Human + ApiToken | TaskWrite | RequireCapability | device proxy | ENFORCED |

### 3.7 Work (`WorkEndpoints.cs`) — H1.1 §4.16

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/work/finish_work` | Human + ApiToken | TaskWrite | RequireCapability | task | REVIEW: creator identity (H2) |

### 3.8 Chat (`ChatEndpoints.cs`) — H1.1 §4.11

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/chat/sessions` | Human only | — | RequireHumanActor | user | ENFORCED |
| POST | `/v1/chat/sessions` | Human only | — | RequireHumanActor | user | ENFORCED |
| GET | `/v1/chat/sessions/{id}` | Human only | — | RequireHumanActor | user | ENFORCED |
| GET | `/v1/chat/sessions/{id}/parts` | Human only | — | RequireHumanActor | user | ENFORCED |
| POST | `/v1/chat/sessions/{id}/prompt` | Human only | — | RequireHumanActor | user | ENFORCED |
| POST | `/v1/chat/sessions/{id}/abort` | Human only | — | RequireHumanActor | user | ENFORCED |
| POST | `/v1/chat/sessions/{id}/parts` | Device only | — | RequireDeviceActor | chat session | ENFORCED |
| POST | `/v1/chat/sessions/{id}/idle` | Device only | — | RequireDeviceActor | chat session | ENFORCED |

### 3.9 Decisions (`DecisionEndpoints.cs`) — H1.1 §4.7

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/projects/{p}/decisions` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/decisions` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/decisions/{id}/accept` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/decisions/{id}/deprecate` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/decisions/{id}/supersede` | Human only | — | RequireHumanActor | project | ENFORCED |

### 3.10 Milestones (`MilestoneEndpoints.cs`) — H1.1 §4.8

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/projects/{p}/milestones` | Human only | — | RequireHumanActor | project | ENFORCED |
| PUT | `/v1/projects/{p}/milestones/{id}` | Human only | — | RequireHumanActor | project | ENFORCED |
| PUT | `/v1/milestones/{id}` | Human only | — | RequireHumanActor | milestone | ENFORCED |
| DELETE | `/v1/projects/{p}/milestones/{id}` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/milestones/{id}` | Human only | — | RequireHumanActor | milestone | ENFORCED |
| GET | `/v1/projects/{p}/milestones` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/milestones/{id}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/milestones/{id}` | Human only | — | RequireHumanActor | milestone | ENFORCED |
| POST | `/v1/projects/{p}/milestones/{id}/close` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/milestones/{id}/reopen` | Human only | — | RequireHumanActor | project | ENFORCED |

### 3.11 Labels (`LabelEndpoints.cs`) — H1.1 §4.9

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| GET | `/v1/projects/{p}/labels` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/labels/match` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/labels/{id}/paths` | Human only | — | RequireHumanActor | project | ENFORCED |

### 3.12 Reports (`ReportEndpoints.cs`) — H1.1 §4.10

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/projects/{p}/reports` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/reports` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/reports/{id}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/reports/context-cost/{taskId}` | Human only | — | RequireHumanActor | task | ENFORCED |

### 3.13 Orgs (`OrgEndpoints.cs`) — H1.1 §4.15

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/orgs` | Human only | — | RequireHumanActor | org | ENFORCED |
| PUT | `/v1/orgs/{orgId}` | Human only | — | RequireHumanActor | org | ENFORCED |
| GET | `/v1/orgs/{orgId}` | Human only | — | RequireHumanActor | org | ENFORCED |
| GET | `/v1/orgs` | Human only | — | RequireHumanActor | orgs | ENFORCED |
| POST | `/v1/orgs/{orgId}/invites` | Human only | — | RequireHumanActor | org | ENFORCED |
| GET | `/v1/orgs/{orgId}/invites` | Human only | — | RequireHumanActor | org | ENFORCED |
| DELETE | `/v1/orgs/{orgId}/invites/{inviteId}` | Human only | — | RequireHumanActor | org | ENFORCED |

### 3.14 Projects (`ProjectEndpoints.cs`) — H1.1 §4.3

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/projects` | Human only | — | RequireHumanActor | project | ENFORCED |
| PUT | `/v1/projects/{p}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/orgs/{orgId}/projects` | Human only | — | RequireHumanActor | org | ENFORCED |
| GET | `/v1/projects` | Human only | — | RequireHumanActor | projects | ENFORCED |
| POST | `/v1/projects/{p}/members` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/projects/{p}/members/{userId}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/members` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/invites` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/invites` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/projects/{p}/invites/{inviteId}` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/tokens` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/tokens` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/projects/{p}/tokens/{tokenId}` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/projects/{p}/tokens/{tokenId}` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/tokens/{tokenId}` | Human only | — | RequireHumanActor | token | ENFORCED |
| GET | `/v1/tokens/{tokenId}` | Human only | — | RequireHumanActor | token | ENFORCED |

### 3.15 Eval (`EvalEndpoints.cs`) — H1.1 §4.14

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/projects/{p}/evals/pair` | Human only | — | RequireHumanActor | project | ENFORCED |

### 3.16 Devices (`DeviceEndpoints.cs`) — H1.1 §4.12

| Method | Route | Actor class | Capability | Enforcement | Resource scope | Status |
|---|---|---|---|---|---|---|
| POST | `/v1/devices` | Human only | — | RequireHumanActor | org/user | ENFORCED |
| GET | `/v1/devices` | Human only | — | RequireHumanActor | user | ENFORCED |
| DELETE | `/v1/devices/{id}` | Human only | — | RequireHumanActor | device | ENFORCED |
| POST | `/v1/devices/me/heartbeat` | Device only | — | RequireDeviceActor | device (own) | ENFORCED |
| GET | `/v1/devices/me/commands` | Device only | — | RequireDeviceActor | device (own) | ENFORCED |
| POST | `/v1/commands/{id}/complete` | Device only | — | RequireDeviceActor | command | ENFORCED |
| POST | `/v1/devices/{id}/commands` | Human only | — | RequireHumanActor | device | ENFORCED |
| GET | `/v1/commands/{id}` | Human only | — | RequireHumanActor | command | ENFORCED |
| GET | `/v1/projects/{p}/runtimes` | Human only | — | RequireHumanActor | project | ENFORCED |
| POST | `/v1/projects/{p}/runtimes` | Human only | — | RequireHumanActor | project | ENFORCED |
| DELETE | `/v1/projects/{p}/runtimes/{id}` | Human only | — | RequireHumanActor | project | ENFORCED |
| GET | `/v1/devices/me/llamaswap-config` | Device only | — | RequireDeviceActor | device (own) | ENFORCED |
| GET | `/v1/devices/me/opencode-connections` | Device only | — | RequireDeviceActor | device (own) | ENFORCED |

## 4. Verification

- **Route scan**: `grep` of `ProjectBeacon.API/Endpoints/*.cs` shows no route mapped with bare `RequireAuthorization()` — every authenticated route carries `RequireCapability`, `RequireHumanActor`, or `RequireDeviceActor`.
- **Integration tests**: `ProjectBeacon.API.Tests/CapabilityWallHttpTests.cs` covers: TaskRead/TaskWrite task gates, ContextRead read gate + human-only context writes, pipeline TaskWrite/TaskRead/Admin force-close, model TaskWrite/TaskRead, finish_work TaskWrite, full-capability token rejected on all human-only domains (orgs, projects, chat, auth self), and human regression (no 403/401 on human-only routes).
- **Application service alignment**: `AuthorizationService.TokenCanAccess` mirrors the wall — Task/TaskStep/Pipeline reads → `TaskRead`, writes/execute → `TaskWrite`; Context reads → `ContextRead`; ChatSession → `Forbidden` (no chat route for tokens); `TokenCan` applies the same Admin bypass as the HTTP filter. Covered by `AuthorizationServiceTests`.

