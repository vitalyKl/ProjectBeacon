# ProjectBeacon Security Model

This document describes the trust boundaries, identity model, and tenant-isolation
mechanisms of ProjectBeacon, and states explicitly what the mechanisms do **not**
protect against. Implementation status per hardening block is tracked in
[`dotnet project docs/ProjectBeacon-Hardening-Roadmap-Updated-2026-10-10.md`](dotnet%20project%20docs/ProjectBeacon-Hardening-Roadmap-Updated-2026-10-10.md);
endpoint-by-endpoint behavior is recorded in `dotnet project docs/ProjectBeacon-authorization-matrix.md`
and `dotnet project docs/ProjectBeacon-endpoint-classification.md`.

## Trust boundaries

ProjectBeacon has three trust domains:

| Domain | Runs | Trusts |
|---|---|---|
| **Control plane** | `ProjectBeacon.Web` host (Blazor Server UI + thin `/v1` API), `ProjectBeacon.Worker` (background expiry jobs) | The database it connects to (see role split below) |
| **Local workstation** | `beacon client` (`ProjectBeacon.Cli`) on the developer's machine: owns the working tree, OpenCode config, and the model process (`UseOwnSwapper` or external llama-swap) | The control plane over outbound HTTPS only (enroll, heartbeat, long-poll commands) |
| **Database** | PostgreSQL | — |

Key boundary rules:

- The workstation connects **outbound only**; there is no inbound tunnel, no hosted clone,
  and no WSS sidecar channel.
- The control plane **does not read the user's disk and does not start model processes**.
  Device commands (`ListDir`, `InitProject`, `ApplyOp`, `SaveWorkstation`, …) execute only
  on the selected online workstation.
- Agents talk to the project through `beacon mcp` (stdio, local) and to the control
  plane through `/v1`; `POST /v1/work/finish_work` is the only way an agent finishes a task.
- The Worker authenticates as the database role and runs **unscoped across projects**;
  it does not execute workstation commands.

## Identity and tokens

All handlers receive a single caller identity — `ActorContext`
(`ProjectBeacon.Application/Authorization/`) — resolved **once** at the HTTP boundary
(`ActorContextFactory.FromPrincipal` / `HttpContext.GetActor()`). Razor components and
Application handlers never read `ClaimsPrincipal` directly and never accept
client-supplied identity fields.

Token types (all shown once at creation, never persisted raw):

| Token | Prefix | Purpose | Storage |
|---|---|---|---|
| Project/org API token | `bcn_` | Machine API access with capability scopes | SHA-256 hash only |
| Device token | `bcd_` | Workstation client enrollment + command polling | SHA-256 hash only |
| Invite token | `bci_` | Registration invitation (required for all SaaS alpha account creation) | SHA-256 hash only |
| Password-reset token | `bcr_` | One-shot password reset link | SHA-256 hash only |
| JWT session | — | Human session cookie; signed with `JWT:Secret` **only** | signed, not encrypted |
| Bootstrap admin token | — | `BOOTSTRAP_ADMIN_TOKEN`; one-time creation of the initial administrator only | env/config only |
| Admin recovery token | — | `ADMIN_RECOVERY_TOKEN`; `/control/recover` break-glass password/MFA recovery, not a user reset; independent of bootstrap | env/config only |

`BEACON_WORKER_TOKEN` is **not** a bearer token: it is an `ActorId` string compared in
constant time, used only by `finish_work` and pipeline force-close. It does not make the
caller an admin of any project, and the Worker process does not read it.

Password hashing is BCrypt in `ProjectBeacon.Infrastructure` (`PasswordHasher`).
Forgot-password always returns 200 (no account enumeration).

## Tenant isolation (defense in depth)

Tenant isolation has two independent layers; either one alone would be sufficient to
hide another tenant's rows, and both are enforced:

1. **EF Core query filters.** Every tenant query goes through `TenantScope`
   (AsyncLocal, carried in DI scopes). Filters are fail-closed: a null
   `FilterProjectId`/`FilterOrgId` returns no rows; `Guid.Empty` matches no tenant.
2. **Postgres row-level security (RLS)** on the application database, enforced for the
   `beacon_app` role regardless of any EF filter bug:
   - **Roles.** `beacon` (Postgres superuser) owns the schema and runs migrations only.
     The application connects as `beacon_app` (`LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB
     NOCREATEROLE`) with per-table GRANTs. At startup,
     `PostgresConnection.EnforceAppRoleAsync` verifies the actually-connected role via
     `current_user` + `pg_roles`; in Production it **aborts** if the role is SUPERUSER,
     BYPASSRLS, or unverifiable (never fail-open). In Development it logs a warning.
   - **GUCs.** On every opened Postgres connection,
     `TenantRlsConnectionInterceptor` copies the scope into `set_config` GUCs:
     `app.tenant_project_id`, `app.tenant_org_id`, `app.tenant_unscoped` (local,
     per-session).
   - **Policies.** Every tenant-scoped table has `ENABLE ROW LEVEL SECURITY` +
     `FORCE ROW LEVEL SECURITY` and a policy built on `USING
     (current_setting('app.tenant_unscoped', true) = 'true' OR <column> =
     current_setting('app.tenant_*', true))` (migrations `EnableRls` and
     `EnableRlsChildTables`). Tables without their own tenant column derive visibility
     from the parent through `EXISTS` subqueries that are themselves RLS-checked:
     - `TaskComments` — visible while the owning `Task` is in scope;
     - `TaskDependencies` — both linked `Task`s must be in scope (a cross-tenant pair is
       invisible from every tenant);
     - `DecisionTasks` — both the `Decision` and the linked `Task` must be in scope.
   - `TenantScope.EnterUnscoped()` is reserved for bootstrap, migrations, tests, and the
     Worker's expired-record cleanup.

Unscoped access is observable in the system (`app.tenant_unscoped = 'true'`), and the
integration tests (`RlsRuntimeRoleTests`, `RlsIsolationTests`, `TenantRebindRlsTests`,
`AppRoleGuardTests` in `ProjectBeacon.API.Tests`) verify RLS behavior against real
migrations and the real `beacon_app` role, including an automatic drift guard that fails
when any tenant-scoped table (direct column or FK to a tenant-scoped table) lacks the
required policy.

### What RLS does NOT protect against

- **Arbitrary SQL run as `beacon_app`.** The policies key off GUCs that the same role can
  set itself: `SELECT set_config('app.tenant_unscoped', 'true', true)` and then any
  query sees every tenant. RLS here is a guard against missing or buggy tenant filters
  in the application query layer, not against a compromised database credential.
  A stolen `beacon_app` password is a full data compromise.
- **Schema owner.** `beacon` (superuser) bypasses RLS by definition; the fail-closed
  startup guard only checks the *application* connection, not the migration connection.
- **In-memory SQLite test hosts.** No RLS boundary exists there; tenant behavior in those
  tests comes from the EF filters only.

## Key separation and secret handling

- `JWT:Secret` signs JWTs and nothing else.
- `DATA_PROTECTION_KEY` (≥ 32 chars, required in non-safe environments, fail-closed
  startup without it) keys the v2 AES-GCM ciphertexts for stored secrets
  (`v2:` prefix, HKDF-SHA256 derived key, AAD-bound). v1 rows are readable only via
  `DATA_PROTECTION_LEGACY_KEY` (absent legacy falls back to a temporary `JWT:Secret`
  bridge with a warning). `DATA_PROTECTION_WRITE_V2` (off by default) switches writes to
  v2; `DataKeyMigration` re-encrypts v1→v2 in place and never masks a failed row.
- Tokens (`bcn_`/`bcd_`/`bci_`/`bcr_`) and keys are shown at most once; never logged or
  printed afterward. Invite/password-reset lookups use `IgnoreQueryFilters` because the
  actor is not in the tenant yet.
Authentication lifecycle and abuse protection:

- `POST /v1/auth/bootstrap` is a one-time operation. The configured
  `BOOTSTRAP_ADMIN_TOKEN` is checked in constant time. The absence check, initial
  administrator insert, and audit event run in one transaction. A process-local async
  gate serializes callers in one host; PostgreSQL hosts additionally take a transaction-
  scoped advisory lock, so two Web replicas cannot both complete bootstrap.
- `POST /v1/auth/recover-admin` is an explicit break-glass path using the separate
  `ADMIN_RECOVERY_TOKEN`; it never falls back to `BOOTSTRAP_ADMIN_TOKEN`. It is not the
  ordinary password-reset flow. It resets the active platform administrator's password,
  clears TOTP/recovery codes, bumps `SessionVersion`, and writes audit events. The
  operation is rate-limited and failures are audited without storing the supplied secret.
  Keep both values high-entropy, distinct, and outside source control; recovery stays
  fail-closed when `ADMIN_RECOVERY_TOKEN` is absent.
- `GET /logout` clears only the current customer browser cookie.
  `POST /control/logout` audits and clears only the current operator Control Plane
  cookie. `POST /v1/auth/logout` is deliberately account-wide: it bumps
  `SessionVersion`, deactivates all active `UserSession` records, and invalidates
  previously issued customer JWT/cookie credentials.
- Sensitive auth endpoints and task attachment uploads use independent fixed-window
  policies. Every policy first partitions by the trusted client IP. After customer
  cookie/JWT authentication, humans also consume a per-user bucket. After `bcn_`
  validation, API tokens consume a per-creator bucket (falling back to token id for a
  token without a creator). Anonymous bootstrap/login/recovery/registration remains
  IP-only because there is no authenticated identity yet. The API token identity stage
  runs after token validation; the early IP gate remains before token lookup.
- Web and standalone API hosts share `ForwardedHeadersOptionsFactory`. Explicit
  `KnownProxies`/`KnownNetworks` replace the framework loopback defaults as the
  complete trust set; untrusted `X-Forwarded-For` values are ignored. Regression tests
  cover trusted/untrusted peers, IP isolation, human identity partitions, API-token
  creator partitions, and independent policy buckets.

## Native agent command execution

The native workstation harness exposes a single argv-based command interface. Its
workstation-local `commandPolicy` is `ask` by default:

- `off` disables command execution tools;
- `ask` requires a user decision for commands that are not covered by a
  previously reviewed local approval for the current manifest/workflow snapshot;
- `auto` skips the approval prompt but still applies the command validator and uses
  the isolated session worktree.

Project manifests and GitHub Actions workflows are **discovery sources, not consent**.
A new project has no trusted manifest fingerprint. Even familiar commands such as
`dotnet test` or `npm test` require the first user decision. The full suggestion list
can be reviewed and selected in one form; only after every displayed suggestion is
selected is that exact manifest/workflow snapshot remembered as trusted. Individual
`always` approvals are also stored locally against the exact snapshot. Changing a
relevant manifest or workflow invalidates both snapshot trust and remembered prefix
approvals, so commands from the changed snapshot require fresh review.

The command runner uses `UseShellExecute=false` and passes an argument vector directly;
it does not interpret shell pipelines or command separators. The workstation environment
passed to child processes is rebuilt from a small allowlist. Beacon tokens, provider API
keys, and the daemon's arbitrary environment variables are not forwarded. `HOME`,
temporary directories, and common package-manager caches are redirected to a per-session
directory. Every command has a timeout, bounded response output, a paged log, and
process-tree termination. Long-running services have explicit start/status/log/stop tools
and are stopped at session completion or abort. Windows child processes are assigned to
a kill-on-job-close job where available; stale service process records are checked on the
next daemon start.

Each native chat prompt and pipeline Actor session uses a detached Git worktree seeded
from a snapshot of the current project files. The original Git index is not modified.
On successful completion, only the agent's diff is applied to the original working tree;
if the turn fails or the patch conflicts, the worktree is retained for recovery. Worktree
creation refuses repositories with configured Git content filters or symbolic links,
and internal Git commands run with hooks disabled.

**This is isolation for correctness and defense in depth, not a security sandbox.** A
command executes with the workstation user's operating-system permissions. Code in a
test, build script, package lifecycle hook, compiler plugin, or application can still
access the network, read files by absolute path, write outside the worktree, or use any
other capability available to that OS account. A malicious repository can also influence
what an approved build or test command does. The deny-list and argv-prefix approvals
catch only obvious mistakes; they do not establish a security boundary. The `auto`
mode therefore skips consent, not the underlying OS permissions. A container with
restricted mounts/network or a dedicated low-privilege OS account is required for a
stronger boundary.

Manifest- and CI-derived prefixes are suggestions for convenience only; they are never
automatically approved on first observation. Changing a relevant manifest or workflow
during an agent turn invalidates manifest trust for that turn, so a newly introduced
command requires a user decision. Project-specific approvals and reviewed fingerprints
are stored locally outside the repository and are never uploaded to the control plane.

In a pipeline Actor turn, a pending command approval keeps the session and subtask in
`WaitingForUser`; the normal idle and absolute turn timeouts are suspended while the
form remains pending. This is an intentional pause, not a failed subtask.

## Structured repository synchronization

Human-only `POST /v1/projects/{projectId}/git-sync` queues a typed `GitSync` command, and human-only `GET /v1/projects/{projectId}/git-sync/{commandId}` returns its project-bound status to the most recently active online workstation attached to that project that advertises the `git-sync-v1` heartbeat capability. The server checks project Owner/Admin membership (or system administrator authority); Both routes require project Owner/Admin membership or system administrator authority; API tokens, device actors, workers, and ordinary project non-members cannot invoke either route. The generic device-command enqueue endpoint explicitly rejects `GitSync`, so it cannot bypass this project-role gate. The payload carries only the project id and a fixed mode. The workstation resolves the configured project root locally and never accepts a path or Git argv from the caller.

The client runs Git without a shell, with hooks redirected to an empty temporary directory, `core.fsmonitor=false`, configured clean/smudge/process filters disabled, bounded output, a timeout, and interactive terminal prompts disabled. It inherits the workstation user's normal credential environment so private remotes continue to use the user's configured Git authentication; environment variables and credentials are not logged or copied into agent context. It fetches only the current branch's configured upstream with tags disabled. The only worktree-changing operation is `git merge --ff-only` after the client verifies a clean tree, attached branch, configured upstream, no divergence, and no active native session. A repository operation gate serializes this against creation and removal of agent snapshots.

Native agents do not receive Git credentials or a raw repository-management tool. `repo_status` is a bounded, read-only status tool for Planner and Actor sessions; `run_command` blocks Git configuration overrides, external execution options, repository-changing subcommands, and `git push`. Repository output is untrusted data.

## Known limitations (open)

- **Single-token revocation.** The current JWT contract uses account-wide
  `SessionVersion`: `POST /v1/auth/logout` revokes all credentials for the account.
  There is no targeted revoke-one-JWT operation. Browser cookie logout remains local to
  that browser. A separate per-session JWT identifier and revoke contract would be needed
  before offering targeted session revocation.
- **`REVIEW REQUIRED` endpoints.** A number of `/v1` endpoints are still marked
  `REVIEW REQUIRED` in the authorization matrix and must not be treated as audited.
- **Hardening blocks H5–H7, H10, H13** are not closed by code evidence alone and remain
  open per the hardening roadmap.
- **Release gate.** The two consecutive full green CI-equivalent runs required for a
  verified release have **NOT** been completed (`NOT VERIFIED`).
