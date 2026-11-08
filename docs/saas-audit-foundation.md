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
Retention, if later required by policy, must use a separate privileged maintenance procedure.

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

`IAdminGate` is implemented in Application and resolves the current alpha
`SystemAdministrator` from persisted `User.IsAdmin` state. The request boundary must call
`IAdminGate.RequireAsync` and pass the returned `AdminActor` to `IAuditReader`.
Requests requiring elevation fail closed until a step-up authentication provider is added.
This foundation does not expose an unauthenticated audit endpoint or operator UI.

## Implemented / not implemented

Implemented: domain event, append-only storage, EF tenant filter, PostgreSQL RLS, atomic staging
port, permission-checked read contract/implementation, model migration and tests.

Not implemented: automatic instrumentation of every existing handler, operator HTTP endpoints,
audit UI, export, retention, or distributed audit streaming. Each sensitive mutation must be
instrumented in its own transaction in follow-up work; this foundation does not claim existing
operations are already comprehensively audited.
