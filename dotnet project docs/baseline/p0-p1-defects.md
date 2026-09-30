# P0 / P1 Defect List

Source of truth: `ProjectBeacon-code-review-roadmap-v3.md` sections 1.1-1.5.
Scope: all 5 trust-boundary vulnerabilities. Full audit + fixes.

## P0 (blocking, must fix before any other work)

### P0-1: Bootstrap fail-open (roadmap 1.1)
- **File:** `ProjectBeacon.Application/Auth/AuthHandlers.cs:31`
- **Bug:** Empty `BOOTSTRAP_ADMIN_TOKEN` skips the check entirely.
- **Fix:** Fail closed. Empty token = reject bootstrap unless env explicitly says `BOOTSTRAP_ENABLED=true`.
- **Test:** Negative test - empty token + no env flag must 401/403.

### P0-2: Unauthenticated logout (roadmap 1.2)
- **File:** `ProjectBeacon.API/Endpoints/AuthEndpoints.cs:24,163-170`
- **Bug:** `POST /v1/auth/logout` is `AllowAnonymous` and accepts client-supplied `UserId`, deleting ALL sessions for that user.
- **Fix:** Require authentication. Derive `UserId` from principal, not body.
- **Test:** Negative test - anonymous request must 401. Authenticated request must only kill own sessions.

### P0-3: API token missing NameIdentifier (roadmap 1.3)
- **File:** `ProjectBeacon.API/Auth/ApiTokenAuthMiddleware.cs:31-35`
- **Bug:** API token adds `token_id`, `project_id`, `capabilities` but NOT `ClaimTypes.NameIdentifier`. Downstream code reading `Principal.Identity.Name` or `NameIdentifier` gets null.
- **Fix:** Add `ClaimTypes.NameIdentifier` claim from token's `UserId`. Create `ActorContext` if not present.
- **Test:** Integration test - API token request must have `NameIdentifier` populated.

### P0-4: Project scope from route/header with token auth (roadmap 1.4)
- **File:** `ProjectBeacon.Infrastructure/Http/TenantIsolationMiddleware.cs:52,57`
- **Bug:** `projectId = fromRoute ?? fromHeader ?? FromClaim`. For API tokens, membership validation is skipped. Client can pass any `projectId` in header/route.
- **Fix:** For API tokens, `projectId` must come from the token's bound project. Route/header `projectId` must match token's project or 403.
- **Test:** Negative test - API token for project A hitting project B endpoint must 403.

### P0-5: Client-supplied ActorId/UserId in request body (roadmap 1.5)
- **Files:** Multiple - FinishWork, ForceClosePipeline, MemberOps, SessionOps, CreateToken, Bootstrap/Invite
- **Bug:** Request bodies accept `ActorId`/`UserId` fields. Server trusts client-supplied value.
- **Fix:** Remove from public request DTOs. Derive actor strictly from `Principal`. Delegated identity (agent/workstation path) authorized via delegation check, not client claim.
- **Test:** Negative test per flow - client-supplied actor must be ignored or 403.

## P1 (important, fix in Phase 1 but not blocking Phase 0 gate)

### P1-1: Minimal UI error states
- No global error boundary for unexpected exceptions.
- No 401/403 redirect handling for expired sessions.
- **Fix:** Blazor `ErrorBoundary` + `AuthInterceptor` for `Fetch`/`HttpClient` 401 responses.

### P1-2: ActorContext does not exist
- Referenced in docs but not in code. Must be created as part of P0-3 fix.
- **Fix:** Create `ActorContext` in `ProjectBeacon.Application` (or `Domain`) with `UserId`, `IsApiToken`, `Capabilities`. Populate in both JWT and API token middleware.

## Verification matrix

| ID | Fix location | Test type | Test location |
|---|---|---|---|
| P0-1 | `AuthHandlers.cs` | Handler (SQLite) | `Application.Tests` |
| P0-2 | `AuthEndpoints.cs` | HTTP (Postgres) | `API.Tests` |
| P0-3 | `ApiTokenAuthMiddleware.cs` | HTTP (Postgres) | `API.Tests` |
| P0-4 | `TenantIsolationMiddleware.cs` | HTTP (Postgres) | `API.Tests` |
| P0-5 | Multiple endpoints | HTTP (Postgres) | `API.Tests` |
| P1-1 | Web Blazor | Manual / Playwright | N/A |
| P1-2 | New `ActorContext` | Handler (SQLite) | `Application.Tests` |
