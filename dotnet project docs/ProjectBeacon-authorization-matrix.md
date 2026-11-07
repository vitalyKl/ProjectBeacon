# ProjectBeacon — Authorization Matrix

> **H1.1 (Hardening Roadmap).** Canonical map of who may read / create / update / delete / execute / administer every resource in the `/v1` control plane.
>
> **Scope of this document: CURRENT BEHAVIOR ONLY.** It describes what the code actually enforces today. It does **not** prescribe what should change. Every broad or surprising permission is recorded with `Status: REVIEW REQUIRED` so that H1.2+ can decide each one individually.

---

## 1. Purpose and status taxonomy

This matrix is the single reference for authorization across the `ProjectBeacon.API` control plane. It exists to:

1. Make the current enforcement model explicit and reviewable.
2. Surface every permission that is broader than the surrounding pattern (so nothing is silently "legitimized").
3. Provide a mechanical cross-check target for later hardening work.

Every authorization statement in this document carries one of these statuses:

| Status | Meaning |
|---|---|
| **CURRENT BEHAVIOR** | What the code enforces today, stated as fact. Neutral. |
| **REVIEW REQUIRED** | Current behavior is broader, weaker, or inconsistent with the surrounding pattern and needs an explicit decision. The 4 fields below must be filled. |
| **TARGET STATE** | Not filled in H1.1. Reserved for the hardening decision. Left as an empty stub throughout. |

For every **REVIEW REQUIRED** item the document records:

- **Current behavior** — what is actually allowed.
- **Current enforcement mechanism** — the route gate + handler check that (or fails to) restrict it.
- **Security / authorization note** — why it is broad or surprising.
- **Status** — `REVIEW REQUIRED`.
- **Target state** — *(empty — decided in H1.2+).*

---

## 2. Actor model

### 2.1 Actor types

`ActorType` (`ProjectBeacon.Application/Authorization/ActorContext.cs`): `Human`, `ApiToken`, `Device`, `Worker`.

The actor is resolved **once** at the HTTP boundary by `ActorContextFactory.FromPrincipal` (JWT → Human, `bcn_` bearer → ApiToken, `bcd_` bearer → Device). Handlers never read `ClaimsPrincipal` directly and never trust client-supplied identity.

| Actor | `UserId` | `DeviceId` | `IsAdmin` | How it authenticates |
|---|---|---|---|---|
| **Human** | always set | null | from user record | JWT (cookie / bearer) |
| **ApiToken** | `CreatedByUserId` (**may be null**) | null | false | `bcn_…` bearer (`ApiTokenAuthMiddleware`) |
| **Device** | null | always set | false | `bcd_…` bearer (`ApiTokenAuthMiddleware`) |
| **Worker** | n/a (no HTTP identity) | n/a | n/a | Postgres DB role + unscoped RLS; **not** an HTTP bearer |

Key identity facts:

- `device_owner_id` is **metadata only** — it is never used to authorize. `NameIdentifier` is **not trusted** for devices (`ActorContextFactory`).
- A `Worker` is the database role plus unscoped RLS across all projects. It is **not** an HTTP actor, so it does not appear in the per-route HTTP matrix below; its access is defined by row-level-security policy, not endpoint gates.

### 2.2 Human roles

`MemberRole` (`ProjectBeacon.Domain/Enums/MemberRole.cs`): `Owner = 0`, `Admin = 1`, `Member = 2`.

Role checks are performed in `ProjectAuthorization` (`ProjectBeacon.Application/Authorization/ProjectAuthorization.cs`):

| Method | Enforced rule (in order) |
|---|---|
| `CanManageProjectAsync` | system-admin bypass → **ApiToken → `false`** → org-owner/admin bypass → project-member `Owner`/`Admin` |
| `CanManageOrgAsync` | system-admin bypass → org-member `Owner`/`Admin` |
| `AddMember` / `RemoveMember` | **ApiToken → `Forbidden`**, then `CanManageProjectAsync` |
| `CreateToken` / `RevokeToken` / `ManageTokens` | **ApiToken → `Forbidden`**, then `CanManageProjectAsync` |

> A **system admin** (`User.IsAdmin`) short-circuits all `CanManage*` checks. There is no system-admin bypass in the HTTP handlers themselves — the bypass lives in `ProjectAuthorization`.

**Per-role access (project scope, current behavior).** `Owner` and `Admin` are equal for the base `CanManageProject` / `CanManageOrg` check (both pass at line 25/35); the differences are the elevated actions below:

| Action | Member | Admin | Owner | System admin |
|---|---|---|---|---|
| Base read (RLS) + task/context/etc. | ✅ | ✅ | ✅ | ✅ |
| `CanManageProject` / `CanManageOrg` (update project, manage members, revoke tokens, list tokens) | ❌ | ✅ | ✅ | ✅ |
| Add member with role `Member`/`Admin` | ❌ | ✅ | ✅ | ✅ |
| **Grant `Owner` role** (AddMember, `ProjectAuthorization.cs:46`) | ❌ | ❌ | ✅ | ✅ |
| Remove a non-Owner member (also: self-leave always allowed) | self only | ✅ | ✅ | ✅ |
| **Remove an `Owner`** (requires >1 owner; `:66–77`) | ❌ | ❌ | ✅ | ✅ |
| Create a token **without** `Admin` cap | ❌ | ✅ | ✅ | ✅ |
| **Create a token with `Admin` cap** (`:97–101`) | ❌ | ❌ | ✅ | ✅ |
| Revoke a token (`RevokeToken`) | ❌ | ✅ | ✅ | ✅ |
| **`ManageTokens`** (`:85`, requires `actor.IsAdmin`) | ❌ | ❌ | ❌ | ✅ |

Notes on the table:
- **ApiToken** is blocked from every management action above (`actor.IsApiToken → Forbidden` in `AddMember` / `CreateToken` / `RevokeToken` / `ManageTokens`, and `CanManage*Async` returns `false` for tokens). Tokens act only through their granted capabilities on the non-management surface (§2.3).
- **System admin** (`User.IsAdmin`) passes all of the above regardless of membership.
- The `ManageTokens` helper (line 83) is system-admin-only but is **not referenced by any handler** (dead code). The actual token HTTP endpoints route through `CreateToken` / `RevokeToken` (project Owner/Admin), so token management in practice is Owner/Admin, not system-admin.

### 2.3 Api-token capabilities

`ApiTokenCapability` (`ProjectBeacon.Domain/Enums/ApiTokenCapability.cs`) is a `[Flags] long`: `TaskRead`, `TaskWrite`, `SessionDrive`, `ContextRead`, `Admin`.

Capability gates work as follows (`ActorRequirementExtensions.HasCapability`):

- If the actor is **not** an `ApiToken` (i.e. Human or Device) → the check **always returns `true`**.
- If the actor **is** an `ApiToken` → it passes only if its granted set includes the required capability, **or** includes `Admin`.

**Consequence:** `RequireHumanOrApiToken(X)` does **not** restrict humans or devices at all — it only restricts api tokens. A human always passes any capability gate. The capability system is a token-scoping mechanism, not a human-authorization mechanism.

### 2.4 The three HTTP enforcement layers

1. **Route gate** — endpoint filters (`ActorRequirementExtensions`): `RequireAuthorization` (401 for anon), `RequireHuman()` (403 for non-humans), `RequireHumanOrApiToken(cap)` (403 for tokens lacking cap; Admin bypasses), `RequireDevice()` (401 for non-devices + adds `AllowDeviceActorAttribute`), `AllowAnonymous`. Each typed actor gate now also emits `RouteActorRequirementMetadata`, used by the generated route inventory.
2. **Device-actor boundary** — `DeviceActorBoundaryMiddleware` runs on every request: if the actor is a `Device` and the endpoint **lacks** the `AllowDeviceActorAttribute` (added by `RequireDevice()`), the request is rejected with **403**. A device can therefore **only** reach the `RequireDevice()` routes.
3. **Handler-level checks** — Application handler role checks:
   - `ProjectAuthorization.*` role checks (see 2.2).
   - `actor.UserId is null → 401`: still present on `RequireHumanOrApiToken` routes where a concrete creator identity is needed (e.g. `finish_work`, model registry).

Because of layer 2, a Device can reach only routes marked with `RequireDevice()`. The current device surface contains five device-management/daemon routes plus three chat ingestion routes: append part, mark idle, and report usage.

---

## 3. A. Resource-group rollup matrix

Legend for the actor columns:

- **H** = Human (any role)
- **H-manage** = Human with a manager role (project `Owner`/`Admin`, or org `Owner`/`Admin`, or system admin)
- **T** = ApiToken (any caps)
- **T(cap)** = ApiToken holding the stated capability (or `Admin`)
- **D** = Device
- **A** = allowed, **A(cap)** = allowed only for tokens holding the cap, **B** = blocked
- **RLS** = visibility further narrowed by tenant / project / user row-level filters in the handler

| Resource group | Read | Create | Update | Delete | Execute / Drive | Administer | Actor reality (today) |
|---|---|---|---|---|---|---|---|
| **Auth (self)** | A (self) | — | A (self) | — | — | bootstrap/recover = break-glass | Human self-service; anon auth surface rate-limited |
| **Version / public** | A | — | — | — | — | — | `AllowAnonymous` |
| **Project (meta)** | A (RLS) | A | A (H-manage) | — | — | members/invites/tokens = H-manage | `REVIEW REQUIRED`: any human can **create** a project; single/list project read is RLS-only |
| **Org (meta)** | A (RLS) | A | A | — | — | invites = H (handler) | `REVIEW REQUIRED`: any authenticated actor can create/update/list orgs (no role gate) |
| **Project membership** | A (RLS) | add = H-manage | — | remove = H-manage | — | — | Human-only (ApiToken forbidden in handler) |
| **Api tokens** | list/get = H-manage | create = H-manage | — | revoke = H-manage | — | — | Human-only (ApiToken forbidden in handler) |
| **Invites** | list = H | create = H-manage | — | revoke = H-manage | — | — | Human-only |
| **Task (core CRUD)** | list/get = A / T(TaskRead); single-get = A (no cap) | create = A / T(TaskWrite) | update = A / T(TaskWrite) | delete = A / T(TaskWrite) | — | — | `REVIEW REQUIRED`: **no role check** on any task mutation — any human, or any token with `TaskWrite`, can create/update/delete |
| **Task (status/claim/substage/deps/review/steps/comments)** | steps = A | steps/comments = A (no cap) | status/claim/substage/deps/review-notes = A (no cap) | step = A (no cap) | — | — | `REVIEW REQUIRED`: all task sub-operations have **no capability and no role gate** |
| **Pipeline** | get = A / T(TaskRead) | start/subtask/review = A / T(TaskWrite) | — | — | approve/force-close | force-close = T(Admin) + H | `REVIEW REQUIRED`: pipeline **approve** needs only `TaskWrite`; `force-close` additionally needs `Admin` cap + human |
| **Context / brief** | all nodes/constraints = A | upsert/import/constraints = A | — | node = A | compile/export = A | — | `REVIEW REQUIRED`: **no capability, no role gate** on the whole group |
| **Decisions** | list/get = A | create = A | accept/deprecate/supersede = A | — | — | — | `REVIEW REQUIRED`: **no capability, no role gate** |
| **Milestones** | list/get = A | create = A | update/close/reopen = A | delete = A | — | — | `REVIEW REQUIRED`: **no capability, no role gate** |
| **Labels** | list/match = A | — | add-path = A | — | — | — | `REVIEW REQUIRED`: **no capability, no role gate** |
| **Reports** | list/get/context-cost = A | generate = A | — | — | — | — | `REVIEW REQUIRED`: **no capability, no role gate** |
| **Chat** | sessions/parts = H | session = H | prompt/abort = H | — | append-part/idle/usage = **D** (`RequireDevice`) | — | Human for drive; device-only stream ingestion routes are explicitly marked `RequireDevice` |
| **Device (registration)** | list = H | create = H | — | revoke = H | — | — | Human-only |
| **Device (commands/runtimes)** | commands = H; runtimes = H | enqueue = H | — | detach = H | heartbeat/claim/complete/config = **D** | — | Device surface is `RequireDevice`; enqueue/attach exclude devices |
| **Model backends** | registry/proxy = H (registry) | upsert = H | bind/unbind/reload = A (no cap) | delete = H | proxy drive | — | `REVIEW REQUIRED`: bind/unbind/reload/proxy have **no capability and no role gate** |
| **Eval** | — | run = H | — | — | — | — | Human-only (`UserId is null → 401`) |
| **Work (finish_work)** | — | — | — | — | finish = H | — | `REVIEW REQUIRED`: `finish_work` needs only a human id — no task/project role check |
| **Worker** | — | — | — | — | — | — | **Not an HTTP actor.** DB role + unscoped RLS, all projects. `BEACON_WORKER_TOKEN` is only an `ActorId` for finish-work / force-close (constant-time compare), never a bearer. |

---

## 4. B. Full endpoint appendix

Columns: **Method** · **Route** · **Source** (file:line) · **Actor-type requirement** · **Required capability** · **Application / handler check** · **Scope** · **Notes / status**.

> Note: The "Application / handler check" column includes inline guards that predate the endpoint-filter consolidation. For `RequireHuman` routes the filter returns 403 before the handler runs, making any `UserId is null → 401` guard unreachable by non-humans. For `RequireHumanOrApiToken` routes the guard remains as a creator-identity check.

Actor-type requirement shorthand (effective, after all three layers):
- **Any-auth** = `RequireHumanOrApiToken(cap)` — Human or ApiToken with the required cap (or `Admin`); **Device blocked** by the device boundary.
- **Human-only** = `RequireHuman()` → 403 (blocks tokens and devices).
- **Human-manager** = Human-only + `ProjectAuthorization` manager check.
- **Token-gated** = tokens further limited by the required capability; humans unaffected.
- **Device** = `RequireDevice()` → 401 + `AllowDeviceActorAttribute` (device boundary pass).
- **Anon** = `AllowAnonymous`.

### 4.1 Auth (`AuthEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/auth/bootstrap` | AuthEndpoints.cs:20 | Anon | — | break-glass admin token | global | `REVIEW REQUIRED`: break-glass; gated by `BOOTSTRAP_ADMIN_TOKEN`, rate-limited |
| POST | `/v1/auth/recover-admin` | AuthEndpoints.cs:21 | Anon | — | break-glass admin token | global | `REVIEW REQUIRED`: break-glass recovery path |
| POST | `/v1/auth/login` | AuthEndpoints.cs:22 | Anon | — | credential check | global | rate-limited (`auth`) |
| POST | `/v1/auth/register` | AuthEndpoints.cs:23 | Anon | — | invite required if `AUTH_LOCAL_INVITE_ONLY` | global | rate-limited |
| POST | `/v1/auth/forgot-password` | AuthEndpoints.cs:24 | Anon | — | always 200 (no enumeration) | global | rate-limited |
| POST | `/v1/auth/reset-password` | AuthEndpoints.cs:25 | Anon | — | reset token (`bcr_`, SHA-256) | global | rate-limited |
| POST | `/v1/auth/change-password` | AuthEndpoints.cs:26 | Human-only | — | `UserId is null → 401` (:127) | self | rate-limited |
| POST | `/v1/auth/logout` | AuthEndpoints.cs:27 | Human-only | — | `UserId is null → 401` (:163) | self | rate-limited |
| GET | `/v1/auth/me` | AuthEndpoints.cs:28 | Human-only | — | `UserId is null → 401` (:176) | self | |
| GET | `/v1/auth/options` | AuthEndpoints.cs:29 | Anon | — | — | global | |
| GET | `/v1/invites/{token}` | AuthEndpoints.cs:30 | Anon | — | invite token lookup (`IgnoreQueryFilters`) | global | rate-limited |
| POST | `/v1/invites/{token}/accept` | AuthEndpoints.cs:31 | Human-only | — | `UserId is null → 401` (:149) | self | |

### 4.2 Version (`VersionEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/version` | VersionEndpoints.cs:11 | Anon | — | — | global | `{ version, gitSha }` |

### 4.3 Projects (`ProjectEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects` | ProjectEndpoints.cs:22 | Human-only | — | `RequireHuman`; creator becomes Owner | tenant | |
| PUT | `/v1/projects/{projectId}` | ProjectEndpoints.cs:23 | Human-manager | — | `RequireHuman`; `CanAsync` Administer (`UpdateProjectHandler`) | project | rename and description |
| DELETE | `/v1/projects/{projectId}` | ProjectEndpoints.cs:24 | Human-manager | — | `RequireHuman`; `CanAsync` Administer (`DeleteProjectHandler`) | project | removes the project and dependent rows |
| GET | `/v1/projects/{projectId}` | ProjectEndpoints.cs:25 | Any-auth | — | RLS only | project | `REVIEW REQUIRED`: single-project read has no explicit role/capability gate (RLS-filtered) |
| GET | `/v1/orgs/{orgId}/projects` | ProjectEndpoints.cs:26 | Any-auth | — | RLS only | org | |
| GET | `/v1/projects` | ProjectEndpoints.cs:27 | Any-auth | — | RLS only | tenant | `REVIEW REQUIRED`: list-all-projects, no role/capability gate |
| POST | `/v1/projects/{projectId}/members` | ProjectEndpoints.cs:29 | Human-manager | — | `RequireHuman`; `ProjectAuthorization.AddMember` (forbids ApiToken) | project | |
| DELETE | `/v1/projects/{projectId}/members/{userId}` | ProjectEndpoints.cs:30 | Human-manager | — | `RequireHuman`; `ProjectAuthorization.RemoveMember` | project | |
| GET | `/v1/projects/{projectId}/members` | ProjectEndpoints.cs:31 | Any-auth | — | RLS only | project | |
| POST | `/v1/projects/{projectId}/invites` | ProjectEndpoints.cs:32 | Human-manager | — | `RequireHuman`; `CanManageProjectAsync` / `CanManageOrgAsync` | project | |
| GET | `/v1/projects/{projectId}/invites` | ProjectEndpoints.cs:33 | Human-only | — | `RequireHuman`; `CanManageProjectAsync` | project | |
| DELETE | `/v1/projects/{projectId}/invites/{inviteId}` | ProjectEndpoints.cs:34 | Human-manager | — | `RequireHuman`; `CanManageProjectAsync` | project | |
| POST | `/v1/projects/{projectId}/tokens` | ProjectEndpoints.cs:36 | Human-manager | — | `RequireHuman`; `ProjectAuthorization.CreateToken` (forbids ApiToken) | project | |
| GET | `/v1/projects/{projectId}/tokens` | ProjectEndpoints.cs:37 | Human-manager | — | `RequireHuman`; `CanManageProjectAsync` | project | |
| GET | `/v1/projects/{projectId}/tokens/{tokenId}` | ProjectEndpoints.cs:38 | Human-manager | — | `RequireHuman`; `CanManageProjectAsync` | project | |
| DELETE | `/v1/projects/{projectId}/tokens/{tokenId}` | ProjectEndpoints.cs:39 | Human-manager | — | `RequireHuman`; `ProjectAuthorization.RevokeToken` | project | |
| DELETE | `/v1/tokens/{tokenId}` | ProjectEndpoints.cs:40 | Human-manager | — | `RequireHuman`; `ProjectAuthorization.RevokeToken` (project = `Guid.Empty`) | project | |
| GET | `/v1/tokens/{tokenId}` | ProjectEndpoints.cs:41 | Human-manager | — | `RequireHuman`; `CanManageProjectAsync` (project = `Guid.Empty`) | project | |

### 4.4 Tasks (`TaskEndpoints.cs`)

All task routes use `RequireAuthorization`. Capability gate where shown; **no role check anywhere in this group**.

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects/{pid}/tasks` | TaskEndpoints.cs:20 | Any-auth | TaskWrite | none | project | `REVIEW REQUIRED`: any human / TaskWrite token can create a task |
| PUT | `/v1/projects/{pid}/tasks/{tid}` | TaskEndpoints.cs:21 | Any-auth | TaskWrite | none | project | `REVIEW REQUIRED` |
| PUT | `/v1/tasks/{tid}` | TaskEndpoints.cs:22 | Any-auth | TaskWrite | none | — | `REVIEW REQUIRED` |
| DELETE | `/v1/projects/{pid}/tasks/{tid}` | TaskEndpoints.cs:23 | Any-auth | TaskWrite | none | project | `REVIEW REQUIRED` |
| DELETE | `/v1/tasks/{tid}` | TaskEndpoints.cs:24 | Any-auth | TaskWrite | none | — | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/tasks` | TaskEndpoints.cs:25 | Any-auth | TaskRead | none | project | |
| GET | `/v1/projects/{pid}/tasks/{tid}` | TaskEndpoints.cs:26 | Any-auth | TaskRead | none | project | |
| GET | `/v1/tasks/{tid}` | TaskEndpoints.cs:27 | Any-auth | **none** | RLS only | — | `REVIEW REQUIRED`: single-task read has **no capability gate** (siblings do) |
| PATCH | `/v1/projects/{pid}/tasks/{tid}/status` | TaskEndpoints.cs:28 | Any-auth | TaskWrite | none | project | |
| PATCH | `/v1/tasks/{tid}/status` | TaskEndpoints.cs:29 | Any-auth | TaskWrite | none | — | |
| PATCH | `/v1/projects/{pid}/tasks/{tid}/substage` | TaskEndpoints.cs:30 | Any-auth | **none** | none | project | `REVIEW REQUIRED` |
| PATCH | `/v1/tasks/{tid}/substage` | TaskEndpoints.cs:31 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| PATCH | `/v1/projects/{pid}/tasks/{tid}/claim` | TaskEndpoints.cs:32 | Any-auth | **none** | none | project | `REVIEW REQUIRED` |
| PATCH | `/v1/tasks/{tid}/claim` | TaskEndpoints.cs:33 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/tasks/{tid}/comments` | TaskEndpoints.cs:34 | Any-auth | **none** | none | project | `REVIEW REQUIRED` |
| POST | `/v1/tasks/{tid}/comments` | TaskEndpoints.cs:35 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| PUT | `/v1/projects/{pid}/tasks/{tid}/dependencies` | TaskEndpoints.cs:36 | Any-auth | **none** | none | project | `REVIEW REQUIRED` |
| PUT | `/v1/tasks/{tid}/dependencies` | TaskEndpoints.cs:37 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| PATCH | `/v1/projects/{pid}/tasks/{tid}/review-notes` | TaskEndpoints.cs:38 | Any-auth | **none** | none | project | `REVIEW REQUIRED` |
| PATCH | `/v1/tasks/{tid}/review-notes` | TaskEndpoints.cs:39 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| GET | `/v1/tasks/{tid}/steps` | TaskEndpoints.cs:40 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| POST | `/v1/tasks/{tid}/steps` | TaskEndpoints.cs:41 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| PATCH | `/v1/steps/{sid}` | TaskEndpoints.cs:42 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |
| DELETE | `/v1/steps/{sid}` | TaskEndpoints.cs:43 | Any-auth | **none** | none | — | `REVIEW REQUIRED` |

### 4.5 Pipeline (`PipelineEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/tasks/{tid}/pipeline` | PipelineEndpoints.cs:15 | Any-auth | TaskRead | none | task | |
| POST | `/v1/tasks/{tid}/pipeline/start` | PipelineEndpoints.cs:16 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/subtasks` | PipelineEndpoints.cs:17 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/subtasks/{sid}/session` | PipelineEndpoints.cs:18 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/sessions/{sid}/launch` | PipelineEndpoints.cs:19 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/subtasks/{sid}/result` | PipelineEndpoints.cs:20 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/subtasks/{sid}/fail` | PipelineEndpoints.cs:21 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/pipeline/review/start` | PipelineEndpoints.cs:22 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/pipeline/verdict` | PipelineEndpoints.cs:23 | Any-auth | TaskWrite | none | task | |
| POST | `/v1/tasks/{tid}/pipeline/review/check` | PipelineEndpoints.cs:32 | Human + ApiToken | TaskWrite | `RequireHumanOrApiToken(TaskWrite)` + `actor.UserId is null → 401`; Human: body `CheckCommand` required (400/Validation if empty); Token: body must be empty (400/Validation if non-empty), resolves from `Project.ReviewCheckCommand` (409/Conflict if null); `CommandSandbox.SanitizeProjectPayload` strips payload `checkCommand`, `EnqueueCommandHandler` injects `TrustedCheckCommand` only | task | |
| POST | `/v1/tasks/{tid}/pipeline/approve` | PipelineEndpoints.cs:25 | Any-auth | TaskWrite | none | task | `REVIEW REQUIRED`: pipeline **approval** requires only `TaskWrite` — no reviewer role |
| POST | `/v1/tasks/{tid}/pipeline/force-close` | PipelineEndpoints.cs:26 | Human-only + token `Admin` | **Admin** | `UserId is null → 401` (:164) | task | `REVIEW REQUIRED`: `force-close` is the only pipeline op requiring the `Admin` cap **and** a human |

### 4.6 Context / brief (`ContextEndpoints.cs`)

**No capability gate, no role gate anywhere in this group** — `RequireAuthorization` + RLS only.

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/projects/{pid}/context/nodes` | ContextEndpoints.cs:14 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/context/nodes` | ContextEndpoints.cs:15 | Any-auth | none | none | project | `REVIEW REQUIRED` (upsert/write) |
| GET | `/v1/projects/{pid}/context/nodes/{nid}` | ContextEndpoints.cs:16 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| DELETE | `/v1/projects/{pid}/context/nodes/{nid}` | ContextEndpoints.cs:17 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/context/import` | ContextEndpoints.cs:18 | Any-auth | none | none | project | `REVIEW REQUIRED` (file import) |
| GET | `/v1/projects/{pid}/context/export/agents-md` | ContextEndpoints.cs:19 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/context/compile` | ContextEndpoints.cs:20 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/constraints` | ContextEndpoints.cs:21 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/constraints` | ContextEndpoints.cs:22 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/constraints/{cid}/activate` | ContextEndpoints.cs:23 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/constraints/{cid}/reject` | ContextEndpoints.cs:24 | Any-auth | none | none | project | `REVIEW REQUIRED` |

### 4.7 Decisions (`DecisionEndpoints.cs`)

**No capability gate, no role gate.**

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects/{pid}/decisions` | DecisionEndpoints.cs:12 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/decisions` | DecisionEndpoints.cs:13 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/decisions/{did}/accept` | DecisionEndpoints.cs:14 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/decisions/{did}/deprecate` | DecisionEndpoints.cs:15 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/decisions/{did}/supersede` | DecisionEndpoints.cs:16 | Any-auth | none | none | project | `REVIEW REQUIRED` |

### 4.8 Milestones (`MilestoneEndpoints.cs`)

**No capability gate, no role gate.**

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects/{pid}/milestones` | MilestoneEndpoints.cs:16 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| PUT | `/v1/projects/{pid}/milestones/{mid}` | MilestoneEndpoints.cs:17 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| PUT | `/v1/milestones/{mid}` | MilestoneEndpoints.cs:18 | Any-auth | none | none | — | `REVIEW REQUIRED` |
| DELETE | `/v1/projects/{pid}/milestones/{mid}` | MilestoneEndpoints.cs:19 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| DELETE | `/v1/milestones/{mid}` | MilestoneEndpoints.cs:20 | Any-auth | none | none | — | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/milestones` | MilestoneEndpoints.cs:21 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/milestones/{mid}` | MilestoneEndpoints.cs:22 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| GET | `/v1/milestones/{mid}` | MilestoneEndpoints.cs:23 | Any-auth | none | RLS only | — | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/milestones/{mid}/close` | MilestoneEndpoints.cs:24 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/milestones/{mid}/reopen` | MilestoneEndpoints.cs:25 | Any-auth | none | none | project | `REVIEW REQUIRED` |

### 4.9 Labels (`LabelEndpoints.cs`)

**No capability gate, no role gate.**

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/projects/{pid}/labels` | LabelEndpoints.cs:12 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/labels/match` | LabelEndpoints.cs:13 | Any-auth | none | none | project | `REVIEW REQUIRED` |
| POST | `/v1/projects/{pid}/labels/{lid}/paths` | LabelEndpoints.cs:14 | Any-auth | none | none | project | `REVIEW REQUIRED` |

### 4.10 Reports (`ReportEndpoints.cs`)

**No capability gate, no role gate.**

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects/{pid}/reports` | ReportEndpoints.cs:13 | Any-auth | none | none | project | `REVIEW REQUIRED` (generate) |
| GET | `/v1/projects/{pid}/reports` | ReportEndpoints.cs:14 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| GET | `/v1/projects/{pid}/reports/{rid}` | ReportEndpoints.cs:15 | Any-auth | none | RLS only | project | `REVIEW REQUIRED` |
| GET | `/v1/reports/context-cost/{tid}` | ReportEndpoints.cs:16 | Any-auth | none | none | task | `REVIEW REQUIRED` |

### 4.11 Chat (`ChatEndpoints.cs`)

Human chat/session routes use `RequireHuman()`. Device ingestion routes use `RequireDevice()`; the endpoint metadata grants the device boundary only for these routes.

| Method | Route | Source | Actor | Cap | Handler / route check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/chat/sessions` | ChatEndpoints.cs:22 | Human | — | `RequireHuman` | user | |
| POST | `/v1/chat/sessions` | ChatEndpoints.cs:23 | Human | — | `RequireHuman` | user | |
| GET | `/v1/chat/sessions/{id:guid}` | ChatEndpoints.cs:24 | Human | — | `RequireHuman` | session | |
| PATCH | `/v1/chat/sessions/{id:guid}` | ChatEndpoints.cs:25 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/archive` | ChatEndpoints.cs:26 | Human | — | `RequireHuman` | session | |
| GET | `/v1/chat/sessions/{id:guid}/parts` | ChatEndpoints.cs:27 | Human | — | `RequireHuman` | session | |
| GET | `/v1/chat/sessions/{id:guid}/events` | ChatEndpoints.cs:28 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/prompt` | ChatEndpoints.cs:29 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/abort` | ChatEndpoints.cs:30 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/agent` | ChatEndpoints.cs:31 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/forms/{formId}/reply` | ChatEndpoints.cs:32 | Human | — | `RequireHuman` | session | |
| POST | `/v1/chat/sessions/{id:guid}/parts` | ChatEndpoints.cs:33 | Device | — | `RequireDevice` | session | |
| POST | `/v1/chat/sessions/{id:guid}/idle` | ChatEndpoints.cs:34 | Device | — | `RequireDevice` | session | |
| POST | `/v1/chat/sessions/{id:guid}/usage` | ChatEndpoints.cs:35 | Device | — | `RequireDevice` | session | |

### 4.12 Devices (`DeviceEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/devices` | DeviceEndpoints.cs:15 | Human-only | — | `UserId is null → 401` (:41) | user | |
| GET | `/v1/devices` | DeviceEndpoints.cs:16 | Human-only | — | `UserId is null → 401` (:52) | user | |
| DELETE | `/v1/devices/{id}` | DeviceEndpoints.cs:17 | Human-only | — | `UserId is null → 401` (:62) | user | |
| POST | `/v1/devices/me/heartbeat` | DeviceEndpoints.cs:18 | Device | — | `RequireDevice`; `DeviceId is null → 401` (:72) | device | |
| GET | `/v1/devices/me/commands` | DeviceEndpoints.cs:19 | Device | — | `RequireDevice`; `DeviceId is null → 401` (:83) | device | |
| POST | `/v1/commands/{id}/complete` | DeviceEndpoints.cs:20 | Device | — | `RequireDevice`; `DeviceId is null → 401` (:95) | command | |
| POST | `/v1/devices/{id}/commands` | DeviceEndpoints.cs:21 | Human-only | — | `UserId is null → 401` **and `IsDevice → 401`** (:106) | command | |
| GET | `/v1/commands/{id}` | DeviceEndpoints.cs:22 | Human-only | — | `UserId is null → 401` (:121) | command | |
| GET | `/v1/projects/{projectId}/runtimes` | DeviceEndpoints.cs:23 | Human-only | — | `UserId is null → 401` **and `IsDevice → 401`** (:131) | project | |
| POST | `/v1/projects/{projectId}/runtimes` | DeviceEndpoints.cs:24 | Human-only | — | `UserId is null → 401` **and `IsDevice → 401`** (:141) | project | |
| DELETE | `/v1/projects/{projectId}/runtimes/{id}` | DeviceEndpoints.cs:25 | Human-only | — | `UserId is null → 401` **and `IsDevice → 401`** (:154) | project | |
| GET | `/v1/devices/me/llamaswap-config` | DeviceEndpoints.cs:26 | Device | — | `RequireDevice`; `DeviceId is null → 401` (:174) | device | |
| GET | `/v1/devices/me/provider-connections` | DeviceEndpoints.cs:27 | Device | — | `RequireDevice`; `DeviceId is null → 401` (:164) | device | |

### 4.13 Model backends (`ModelEndpoints.cs`)

Capability gate `TaskWrite`/`TaskRead` on the route. Registry/proxy reads are human-only; bind/unbind/reload have **no** role or human gate.

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| GET | `/v1/models` | ModelEndpoints.cs:14 | Human-only | TaskRead | `UserId is null → 401` (:28) | user | model registry read |
| POST | `/v1/models` | ModelEndpoints.cs:15 | Human-only | TaskWrite | `UserId is null → 401` (:39) | user | upsert model backend |
| DELETE | `/v1/models/{id}` | ModelEndpoints.cs:16 | Human-only | TaskWrite | `UserId is null → 401` (:53) | user | |
| POST | `/v1/models/bind` | ModelEndpoints.cs:22 | Human + ApiToken | TaskWrite | `RequireHumanOrApiToken`; Application requires Project Administer for humans / TaskWrite for tokens | project | API and direct handler share the same policy |
| DELETE | `/v1/models/bind/{role}` | ModelEndpoints.cs:23 | Human + ApiToken | TaskWrite | `RequireHumanOrApiToken`; Application requires Project Administer for humans / TaskWrite for tokens | project | API and direct handler share the same policy |
| GET | `/v1/models/proxy/status` | ModelEndpoints.cs:19 | Any-auth | TaskRead | **none** (handler reads no actor) | project | `REVIEW REQUIRED` |
| POST | `/v1/models/proxy/reload` | ModelEndpoints.cs:20 | Any-auth | TaskWrite | **none** (handler reads no actor) | project | `REVIEW REQUIRED` |
| POST | `/v1/models/proxy/unload` | ModelEndpoints.cs:21 | Any-auth | TaskWrite | **none** (handler reads no actor) | project | `REVIEW REQUIRED` |

### 4.14 Eval (`EvalEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/projects/{projectId}/evals/pair` | EvalEndpoints.cs:11 | Human-only | — | `UserId is null → 401` (:23) | project | human-only (`StartPair`) |

### 4.15 Orgs (`OrgEndpoints.cs`)

**No capability gate, no role gate** except the `UserId` guard on the three invite routes.

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/orgs` | OrgEndpoints.cs:20 | Any-auth | none | none (`CreatedByUserId = actor.UserId`, may be null) | tenant | `REVIEW REQUIRED`: **any** authenticated actor can create an org |
| PUT | `/v1/orgs/{orgId}` | OrgEndpoints.cs:21 | Any-auth | none | none | org | `REVIEW REQUIRED`: **any** authenticated actor can update any org |
| GET | `/v1/orgs/{orgId}` | OrgEndpoints.cs:22 | Any-auth | none | RLS only | org | `REVIEW REQUIRED` |
| GET | `/v1/orgs` | OrgEndpoints.cs:23 | Any-auth | none | RLS only | tenant | `REVIEW REQUIRED`: list-all-orgs, no role gate |
| POST | `/v1/orgs/{orgId}/invites` | OrgEndpoints.cs:24 | Human-only | none | `UserId is null → 401` (:76); `CanManageOrgAsync` (InviteHandlers.cs:37) | org | |
| GET | `/v1/orgs/{orgId}/invites` | OrgEndpoints.cs:25 | Human-only | none | `UserId is null → 401` (:88); `CanManageOrgAsync` (InviteHandlers.cs:196) | org | |
| DELETE | `/v1/orgs/{orgId}/invites/{inviteId}` | OrgEndpoints.cs:26 | Human-only | none | `UserId is null → 401` (:99); `CanManageOrgAsync` (InviteHandlers.cs:241) | org | |

### 4.16 Work (`WorkEndpoints.cs`)

| Method | Route | Source | Actor | Cap | Handler check | Scope | Notes / status |
|---|---|---|---|---|---|---|---|
| POST | `/v1/work/finish_work` | WorkEndpoints.cs:15 | Human-only | — | `UserId is null → 401` (:27) | task | `REVIEW REQUIRED`: `finish_work` only needs a human id — **no** task/project role or capability check; completes any task by `TaskId` |

---

## 5. Inconsistencies found while mapping

These are places where the three enforcement layers disagree or are applied unevenly. Each is `REVIEW REQUIRED`.

1. **`GET /v1/tasks/{tid}` has no capability gate** while its in-project sibling `GET /v1/projects/{pid}/tasks/{tid}` requires `TaskRead` (`TaskEndpoints.cs:26` vs `:27`). *Status: REVIEW REQUIRED.*

2. **Task sub-operations (status / claim / substage / dependencies / review-notes / steps / comments) have no capability gate at all**, even though task CRUD requires `TaskWrite`. A token with only `TaskRead` can still claim/advance/comment on tasks. *Status: REVIEW REQUIRED.*

3. **Model bind/unbind/reload/proxy routes have no human (`UserId`) gate**, while the model registry read and backend create/delete in the same file do. *Status: REVIEW REQUIRED.*

4. **`POST /v1/projects` and `POST /v1/orgs` accept any authenticated actor** (including api tokens) with no role or capability check, and record `CreatedByUserId = actor.UserId` (null for tokens/devices). *Status: REVIEW REQUIRED.*

5. **`finish_work`** is gated only by a non-null human id; there is no check that the actor is a member of, or manager of, the project containing the target `TaskId`. *Status: REVIEW REQUIRED.*

---

## 6. Route inventory cross-check

The route inventory is generated from the mapped `EndpointDataSource`, rather than maintained as an independent route count. This catches routes added after the manual resource-level review above and reports each HTTP method, route template, and route actor policy. The generated inventory is exercised by `EndpointAuthorizationInventoryTests`; any `/v1` route without explicit anonymous, authenticated, or typed actor metadata fails the test.

The resource-group rollup and handler-level findings in §§3–5 remain a separate authorization review: route metadata does not establish project membership, resource ownership, tenant scope, or authorization performed inside Application handlers.

## 6.e Generated route-policy inventory

The route-level matrix can be generated from the mapped API endpoints with `EndpointAuthorizationInventory.Create(EndpointDataSource)` and rendered with `EndpointAuthorizationInventory.ToMarkdown(...)`. The inventory records method, route template, and the declared route policy (`Anonymous`, `Authenticated`, typed actor, and optional capability). `RequireHuman`, `RequireDevice`, `RequireApiToken`, and `RequireHumanOrApiToken` emit typed metadata; routes with neither `AllowAnonymous`, authorization metadata, nor typed actor metadata are marked `UNCLASSIFIED`. `EndpointAuthorizationInventoryTests` asserts that every mapped `/v1` route is classified and checks representative human/device/anonymous routes. This is the mechanical route inventory; the resource-specific handler checks and the `REVIEW REQUIRED` findings in §3–§5 remain a separate manual authorization review.

---

## 7. TARGET STATE (stub)

*Empty by design. H1.1 records current behavior only. Each `REVIEW REQUIRED` item above will receive its target decision in the relevant H1.2+ work item.*
