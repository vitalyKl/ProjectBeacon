# Audit foundation (A1)

## Data model

`AuditEvents` is an append-only event journal. It deliberately has no foreign keys to users,
organizations, or projects: deleting an account or tenant must not erase the evidence of earlier
security and operator actions.

Each event records UTC time, actor kind and optional actor principal id, an action code, target
type/id, optional organization/project scope, outcome, request IP/user-agent/trace id, and a
small allowlisted details object.

Project-scoped events must also carry their organization id. A project tenant sees and writes
only events whose organization and project both match its bound scope. An organization-level
tenant can read its entire organization's history but can write only organization-level events
(without a project id). Platform-wide events have neither tenant id and are visible only in an
explicit unscoped operator path. EF query filters and PostgreSQL RLS enforce these boundaries;
RLS is forced even for the table owner.

## Integrity and privacy

The table has a PostgreSQL trigger that rejects UPDATE and DELETE. Events are inserted only.
No automatic retention or deletion job is enabled: the product has not yet selected a contractual
or regulatory retention period. Any future purge must be a separate, explicitly authorized
maintenance procedure, never a regular application write.

Details JSON is limited to 4 KiB and to scalar values under a fixed allowlist of keys. String
values must be short machine-readable codes; free-form text, credentials, payment data, chat
messages, task descriptions, context contents, source code, and request/response bodies are
not valid audit details. User-agent and IP are metadata only and must be handled under the
product's privacy/retention policy.

There are no cascading foreign keys. Actor and target identifiers remain useful after the
referenced record is deleted without retaining the referenced record itself.

## Application contracts

- `IAuditWriter.Stage(IBeaconDb, AuditEvent)` adds an event to the caller's existing unit of
  work. The same `SaveChangesAsync` persists both the business mutation and audit event; a
  rollback therefore removes both.
- `IAuditReader` accepts a non-forgeable `AdminActor`, requires `AuditRead`, and returns
  bounded, deterministic pages. The reader uses an explicit unscoped scope only after that
  authorization object has been supplied.
- `IAdminGate` resolves platform-operator authority from persisted operator-role assignments and
  returns the non-forgeable `AdminActor`. The legacy `User.IsAdmin` marker remains true only for
  `SystemAdministrator`; caller-supplied roles or permissions are not trusted.

The Control Plane resolves the persisted `SystemAdministrator`, `Support`, `Billing`, and
`Auditor` roles through `IAdminGate`. Read and export are separate permissions (`AuditRead` and
`AuditExport`). High-risk mutations require a current ten-minute TOTP step-up elevation.

## Instrumented operations

The audit writer stages these events in the same database unit of work as the relevant mutation:

| Area | Actions |
|---|---|
| API tokens | `project.api_token.created`, `project.api_token.revoked` |
| Project invitations | `project.invite.created`, `project.invite.expired`, `project.invite.accepted`, `project.invite.revoked` |
| Organization invitations | `org.invite.created`, `org.invite.expired`, `org.invite.accepted`, `org.invite.revoked` |
| Membership | `project.member.added`, `project.member.removed` |
| Organizations | `org.created`, `org.updated` |
| Projects | `project.created`, `project.updated`, `project.deleted` |
| Devices | `device.enrolled`, `device.token.rotated`, `device.revoked` |
| Project runtimes | `project.runtime.attached`, `project.runtime.updated`, `project.runtime.detached` |
| Account credentials | `account.registered` (open and invited registration), `account.login.succeeded`, `account.login.denied`, `account.logout`, `account.password_reset.requested`, `account.password_reset.completed`, `account.password_changed` |
| Multi-factor authentication | `account.mfa.setup_started`, `account.mfa.enabled`, `account.mfa.disabled`, `account.mfa.recovery_codes_rotated`, `account.mfa.recovery_code_used` |
| Platform bootstrap and recovery | `platform.bootstrap.denied`, `platform.bootstrap.completed`, `platform.admin_recovery.denied`, `platform.admin_recovery.failed`, `platform.admin_recovery.completed` |
| Control Plane authentication | `platform.control_login.denied`, `platform.control_login.succeeded`, `platform.control_logout.succeeded` for operator-console sign-in attempts, successes, and explicit sign-out |
| Authorization | `security.authorization.denied` for explicit HTTP 403 responses on `/v1` routes |

Login-denial events use a generic target for unknown accounts and machine-readable reason codes;
they never store the submitted login, email address, password, password-reset token, TOTP code,
device fingerprint, raw device token, or API token. Break-glass recovery events likewise store
only a fixed reason/result code and, on success, the recovered administrator's id; the bootstrap
token and generated password are never recorded. A generic 403 event stores only the route
template and HTTP method, actor identifier when available, bounded request metadata and a fixed
reason code. It never captures request bodies or query-string values.

Successful administrator recovery stages its audit event in the same database unit of work as
the password reset. Rejected recovery attempts are recorded in a separate platform-scoped write
because no account mutation occurs; failure to persist such a denial event does not replace the
original authentication response.

Invitations, account registration through invitations, token changes, and membership operations
retain their existing transactional instrumentation. Organization-level mutations use a tenant
context bound to the target organization with no inherited project scope. Device and account
operations use explicit global scopes rather than relying on an ambient tenant selection.

## Operator API and console

The platform-operator HTTP API exposes:

- `GET /v1/admin/audit` — filtered, bounded pages (up to 200 rows) with organization, project,
  UTC time range, action, actor kind/user/token/device id, target type/id, exact trace ID, and
  outcome filters.
- `GET /v1/admin/audit/export.csv` — CSV using the same filters, including exact trace ID,
  separately gated by `AuditExport`. If more than 10,000 events match, the endpoint returns `413 Payload Too Large`
  and requires narrower filters; it never silently exports a partial result. CSV fields are
  quoted, formula-prefixed values are neutralized, and the response is marked `no-store` and
  `nosniff`.

Both endpoints require an authenticated human and an `AdminActor` returned by `IAdminGate`.
The Blazor operator console is hosted at `https://control.projectbeacon.tech/admin/audit`, not
under the customer application's origin. The customer Web host returns `404` for operator,
bootstrap, and break-glass routes. The standalone public API host does not serve operator
endpoints at all. The Web host exposes the audit API only when the request uses the configured
Control Plane hostname. See `docs/control-plane-access.md` for the route and deployment boundary.

Operators sign in on `/control/login` through the separate
`POST /v1/auth/control-cookie-login` endpoint. A successful password check issues the host-only
`BeaconControlAuth` cookie only to a persisted platform administrator. The Control Plane
authentication selector does not accept the customer application's `BeaconAuth` cookie or a
customer JWT as an alternate operator session. The audit page is explicitly bound to that cookie
scheme and each API operation rechecks persisted operator rights through `IAdminGate`.
Operator sessions use the separate host-only `BeaconControlAuth` cookie. All admitted operator
roles are resolved from persisted role assignment; the customer `BeaconAuth` cookie and customer
JWT are not accepted as Control Plane sessions. TOTP enrollment, one-time recovery codes, and
step-up elevation are implemented independently of the customer login session.

Audit events are written by server-side application handlers, not through a public client-writable
audit endpoint. Bootstrap and administrator recovery are also Control Plane-only operations.

## Verification

Tests cover permission-denied reads, operator reads and export, denial-event persistence,
CSV formula-injection and row-limit protections, organization/project/runtime lifecycle,
device enrollment/token rotation/revocation, logout/session invalidation, open and invited
registration, invitation flows, machine-actor mapping, break-glass recovery success/denial,
and the absence of credentials, invite secrets or local paths from event details. The
PostgreSQL RLS and append-only trigger tests remain the database-level enforcement layer.

## Remaining scope

The alpha audit console and the primary organization/project/device/account/platform-recovery
security lifecycle are implemented. Deliberately not included in this slice:

- Automated retention/deletion. A retention period must be chosen with product, privacy and legal
  requirements; until then the application does not purge audit rows.
- Distributed event streaming. PostgreSQL is the durable source of truth for this single-service
  alpha. Should external fan-out become necessary, publish through a transactional outbox rather
  than publishing directly from handlers.
- Full event capture for every task, chat, context, pipeline and read operation. High-volume
  content events are not audit records; any new action must be explicitly selected and use the
  fixed metadata allowlist.
- Full metrics/tracing aggregation and Kubernetes-native pod-log access remain outside this
  alpha slice. B4 metadata heartbeats and restricted Docker Compose/VPS Web/Worker/Migrate log
  tails are implemented behind `OperationsRead` + `ContainerLogsRead`; the audit journal records
  successful log-read metadata but is deliberately not a high-volume diagnostic log.
