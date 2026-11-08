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

## Instrumented operations (A2)

The first write-path integration records the following successful project operations in the
same unit of work as their state changes:

- API token creation and revocation (`project.api_token.created`,
  `project.api_token.revoked`). Raw token values and token hashes are never written.
- Project invitation creation, expiration, acceptance and revocation (`project.invite.*`). Invite
  tokens, email addresses and mail bodies are never written.
- Project membership addition and removal (`project.member.added`,
  `project.member.removed`). The event identifies the membership row and uses only fixed state
  codes in details.

Each event carries the verified actor kind/id and both organization and project scope. Tests
verify that the event and mutation persist together and that secret values do not enter the
event metadata.

Organization provisioning now records `org.created` and `org.updated` in the same org-bound
unit of work as the organization mutation. The operator is not added to customer membership as
a side effect; the customer owner joins through the invitation workflow.

This slice extends coverage to organization invitation creation, expiration, acceptance and
revocation. First-time registration through an organization or project invitation also stages
both the invite-accepted and account-registered events in the same tenant-bound transaction as
user creation and membership changes. Invite-token acceptance resolves the opaque token and, for project invitations, its target
project scope in a narrow unscoped context, then reopens the invite row in a context bound to the
invitation's actual organization/project. The signed-in user is resolved in the caller's normal
context.
The membership changes and audit event are staged together in that scoped unit of work. A
caller's previous tenant cannot redirect the event or cause the audit writer to reject a valid
invite acceptance.

## Implemented / not implemented

Implemented: domain event, append-only storage, EF tenant filter, PostgreSQL RLS, atomic staging
port, permission-checked read contract/implementation, model migration, tests, and transactional
instrumentation for organization creation/update, project API tokens, project invitations,
project membership, organization invitations, and first-time account registration through an
invitation.

Not implemented: instrumentation of remaining org/project lifecycle operations (such as
deletion), device lifecycle, other account/password operations and denied authorization attempts,
operator HTTP endpoints, audit UI, export, retention, or distributed audit streaming. These remain separate
follow-up slices; this PR does not claim comprehensive audit coverage.
