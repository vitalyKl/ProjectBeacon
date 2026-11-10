# ProjectBeacon SaaS operator foundation

**Status:** The isolated Control Plane, A1 audit console, platform SMTP settings, B1 organization/owner-invitation/Alpha-entitlement workflow, persisted SystemAdministrator/Support/Billing/Auditor roles, TOTP step-up and one-time recovery codes for every operator role are implemented in the current development stack. B4 metadata-only Web/API/Worker heartbeats and the OperationsRead dashboard are implemented; restricted external container-log access remains incomplete. B2 has a persisted provider-neutral BillingPlan catalog and Control Plane editor. B3 now persists organization subscriptions and an append-only billing ledger under tenant/operator FORCE RLS and implements an atomic Alpha → Production entitlement conversion with TOTP elevation, audit writes and idempotent replay. B3 also includes an operator-only manual invoice workflow with server-generated invoice numbers, due dates, and Open/Paid/Void/Uncollectible states. Invoice state transitions write billing-ledger entries and audit events in the same transaction. Marking an invoice paid stores an operator-entered external receipt reference only; it does not verify funds received or reconcile payments. Customer billing is available to active-organization Owners/Admins at `/billing`, with a tenant-bound invoice detail view and browser Print / Save PDF flow. The print page is a copy of the manual record, not a tax/fiscal invoice; server-generated PDF artifact delivery, payment-method storage and provider adapters remain incomplete. The `projects` capacity key is enforced atomically at project creation. The `members` key reserves active organization members plus unexpired org invitations, including operator-issued owner invitations; customer and Control Plane invite create/accept/revoke operations share one organization lock, and acceptance rechecks the member limit before adding membership. Omitted keys are unmetered; explicit zero blocks new usage. Broader per-module feature gates and additional usage counters remain incomplete. Owner/Admin-requested ZIP export v1 covers organization/workspace content (including task phases, task workspace sections, project review commands and role bindings, chat content and task attachments) while excluding credential stores, local workstation paths, pipeline/review/evaluation history and platform audit/security internals. Individual data-subject export, deletion workflows, retention schedules and physical/pseudonymous erasure remain incomplete. The primary SystemAdministrator retains deployment-token-protected break-glass recovery.

## Product boundary

ProjectBeacon is a hosted, multi-tenant SaaS. The service operator provisions and supports customer organizations from one operator console.

Customer self-hosted deployments, self-host licensing, offline activation, and per-installation billing are out of scope. Docker Compose and local PostgreSQL remain development/test infrastructure, not a supported customer deployment model.

The first external release is an operator-managed alpha. Public self-service registration is disabled. A customer receives an organization invitation from the operator, creates the first user through that invitation, and joins the same organization that will later become a paying production customer.

## Lifecycle invariant

An alpha organization must not be recreated when it becomes commercial. Its OrgId, users, memberships, projects, devices, tokens, and customer data remain intact.

Organization lifecycle, access entitlement, and billing are separate concerns:

- Organization lifecycle answers whether the organization exists and is operationally open or closed.
- Entitlement answers which product capabilities and limits are currently available to that organization.
- Subscription and invoices answer the commercial relationship, price, payment state, and billing periods.

Alpha and Production both resolve to the same effective entitlement contract. `IEntitlementService.ConvertAlphaToProductionAsync` is implemented as one atomic application operation. It locks the active organization, requires a persisted EntitlementsManage operator plus current TOTP elevation, verifies an active product plan and an effective AlphaProgram grant, creates a ManualInvoice subscription, replaces the Alpha entitlement with a Subscription-backed entitlement, appends a billing ledger activation event, and stages audit events in one PostgreSQL transaction. Exact retries return the original entitlement; conflicting idempotency-key reuse fails. The operation stores an operator-supplied manual invoice/receipt reference only: it does not itself verify receipt of funds or create an invoice document. Failed transitions roll back without dropping alpha access.

## Operator authorization contract

Application contracts live in ProjectBeacon.Application/Admin:

- OperatorPermission is a set of named capabilities, not an operator role or a claim.
- AdminActor is the verified operator identity passed to privileged handlers. Its constructor is internal; callers obtain it only from IAdminGate.
- IAdminGate is responsible for resolving a human actor against current database state and checking requested permissions and elevation.

For the alpha, the existing `User.IsAdmin` flag maps to `OperatorPermissionSets.CurrentSystemAdministrator`. Stable role keys are reserved now: `SystemAdministrator`, `Support`, `Billing`, and `Auditor`. Their starter permission sets live in `OperatorRolePermissionSets`; Support has customer/account and operational support but no billing mutation, Billing has commercial/entitlement operations but no account administration, and Auditor is read-only. All four roles are persisted on the user account and assignable through the Control Plane by an elevated SystemAdministrator. The legacy `IsAdmin` flag remains set only for SystemAdministrator; `IAdminGate` resolves the persisted role and derives the effective permission set on every request. Role changes invalidate the target account sessions. The last active SystemAdministrator cannot be removed or demoted, and an operator cannot change their own role.

Role assignment is stored on the user account and changed only through `OperatorRoleAdminService`. Handlers continue asking for a permission, not checking a role name or User.IsAdmin directly. Project MemberRole and ApiTokenCapability remain unrelated security domains. API tokens, devices, and workers never receive operator permissions.

Sensitive mutations require an elevated AdminActor. The database-backed gate validates the current operator account, its persisted TOTP-enabled state, and the signed Control Plane elevation expiry on each invocation; a Blazor page authorization attribute is only a UI guard.

## Organization entitlements

Application contracts live in ProjectBeacon.Application/Entitlements:

- OrganizationEntitlement is the effective product-access snapshot.
- EntitlementSource distinguishes AlphaProgram, Subscription, Complimentary, and OperatorOverride.
- EntitlementStatus is independent of organization lifecycle and subscription payment state.
- Limits and feature keys are ProjectBeacon-owned stable keys. Do not store provider price identifiers as feature names.
- IEntitlementService is the single read/write boundary for effective access and operator-issued grants.

Do not choose commercial units prematurely. Limits are keyed values so the product can later decide whether it sells seats, projects, devices, usage, or bundles without changing the shape of authorization calls.

Alpha access is a time-bounded or open-ended AlphaProgram entitlement issued by an operator. It is not a special organization type and does not depend on a payment provider.

When paid subscriptions arrive, the billing event handler creates or updates the Subscription-backed entitlement. The effective-access resolver remains the same. The conversion operation must be atomic: close/replace the active alpha grant, persist the subscription state and billing event, and publish the new entitlement in one database transaction. Failed transitions must leave the existing alpha access unchanged.

## Billing provider contract

Application contracts live in ProjectBeacon.Application/Billing:

- PlanDefinition, OrganizationSubscription, BillingInvoice and BillingLedgerEntry are provider-neutral product/commercial snapshots. Subscription status and billing cadence are ProjectBeacon-owned; provider statuses must be mapped at the adapter boundary.
- IBillingProvider defines checkout, customer portal, hosted payment-method setup/list/default/detach, subscription retrieval/change/cancellation, and verified webhook normalization.
- IBillingProviderResolver selects an adapter by an application-owned provider key.
- BillingProviderEvent is a normalized event with an external event ID for idempotency. Provider SDK objects and raw webhook payloads do not cross the adapter boundary.
- BillingCheckoutRequest refers to a stable PlanKey, not a provider-specific price ID.
- Amounts are integer minor units and currencies are ISO 4217. A plan can exist without a configured price during alpha; no price, cadence or licensing unit is assumed by the contract.

No provider is selected or registered for the alpha. There is no provider SDK dependency in Domain or Application and no provider-specific endpoint yet. Selecting a provider later adds an Infrastructure adapter and configuration, not changes to handlers or the entitlement contract.

Payment collection should use provider-hosted checkout/portal and payment-method setup sessions. `IBillingProvider` reserves setup, list, set-default and detach operations; the browser collects payment details directly with the provider. ProjectBeacon must never receive or persist full card numbers, CVV/CVC, magnetic-stripe data, bank login credentials, or equivalent raw payment credentials.

IPaymentMethodReferenceStore is reserved for opaque provider-owned references and masked presentation metadata only (brand, last four digits, expiry). A provider reference must be encrypted at rest if its provider treats it as a credential. Provider secrets and webhook-signing secrets belong in deployment secret management, never in organization rows, application settings committed to source, audit records, or logs.

Billing events are append-only and idempotent on (ProviderKey, ExternalEventId). Monetary amounts use integer minor units and ISO 4217 currency codes. Refunds are reflected as verified events; the operator console must not invent a money-moving refund API.

## Registration contract

Application contracts live in ProjectBeacon.Application/Auth/RegistrationPolicy.cs:

- InviteOnly is the only active SaaS alpha mode.
- ApprovalRequired reserves a flow in which a request remains pending until an operator approves it.
- OpenWithVerifiedEmail reserves future self-service registration and must require verified email before an account or organization is activated.

Only InviteOnly is implemented in RegisterHandler today. The general-purpose customer `POST /v1/orgs` endpoint also rejects self-service organization creation whenever InviteOnly is active; deployed environments cannot disable this policy. New Alpha organizations are provisioned through the operator Control Plane, which creates the owner invitation in the same transaction. Every deployed environment is fail-closed and ignores the legacy AUTH_LOCAL_INVITE_ONLY opt-out. A narrowly scoped opt-out is available only when ASPNETCORE_ENVIRONMENT is explicitly Testing; it exists to support isolated API integration tests and cannot be activated in Development, Staging, or Production. The enum values for future modes are contracts only; do not expose them as configuration options until their complete handlers, email verification, rate limits, abuse controls, tenant-creation rules, audit events, and tests exist.

The alpha flow is operator-created organization → organization-owner invitation → invited owner registration. Creating a user without a valid invitation is not an alpha onboarding path.

## Data and extension points for later phases

Alpha entitlement grants now have PostgreSQL persistence in `OrganizationEntitlements`, an operator/tenant RLS policy, transactional grant/revoke writes, and audit events. Every operator-issued grant must include the stable `core` feature. The table is managed through the PostgreSQL entitlement service rather than an EF entity; it is covered by the runtime-role RLS tests.

The provider-neutral `BillingPlan` catalog is implemented in `BillingPlans` with operator-only FORCE RLS, a Control Plane editor and SQLite/PostgreSQL authorization coverage. Organization subscription and billing-ledger persistence are implemented in `OrganizationSubscriptions` and `BillingLedgerEntries`; both use tenant/operator FORCE RLS. The ledger also has provider-event/idempotency uniqueness, a same-organization subscription FK and a database append-only trigger. The remaining commercial persistence models are listed below; future work must include reviewed migrations, RLS/grants and PostgreSQL tests:

| Future model | Responsibility | Required invariant |
|---|---|
| ~~Plan~~ | Implemented as `BillingPlan` / `BillingPlans`; price and currency remain nullable and no provider IDs are stored | Product-owned key, features and limits; provider identifiers never define plan identity |
| ~~Subscription~~ | Implemented as `OrganizationSubscriptions`; source, status, product plan and current period are stored | At most one non-terminal subscription per organization; tenant/operator RLS |
| ~~BillingEvent~~ | Implemented as append-only `BillingLedgerEntries`; supports application and future provider event types | Immutable trigger, provider-event idempotency, operation idempotency and tenant/operator RLS |
| ~~Invoice record~~ | Implemented as `BillingInvoices`: generated `PB-YYYY-######` number, description, amount/currency, issue/due dates and Open/Paid/Void/Uncollectible states; state transitions write ledger and audit events atomically | Paid state stores an operator-entered external reference only; it does not verify receipt of funds or reconcile payments |
| Invoice document/delivery | Implemented: tenant-bound printable customer invoice record at `/billing/invoices/{id}` with browser Print / Save PDF. Not implemented: server-generated PDF artifact and email delivery | Printed copy is informational, not a tax/fiscal invoice; delivery retries and jurisdiction-specific invoice requirements remain future work |
| PaymentMethodReference | Provider reference and masked metadata | No PAN/CVV or provider secret token |

RLS scopes Alpha entitlement grants by OrgId. Future organization-owned billing rows must use the same isolation rule. Operator-wide reads must use the reviewed admin data-access boundary, not switch the current Blazor tenant. Audit writes for operator mutations and billing state transitions must be atomic with the corresponding database change.

## Delivery order

1. **A0 — access revocation:** DisabledAt, SessionVersion, cookie/JWT validation and circuit revalidation.
2. **A1 — audit core:** append-only AuditEvents, transactional Stage writes, redaction and security-event coverage.
3. **A2 — operator gate:** database-backed IAdminGate, AdminActor creation, TOTP/elevation, admin-only navigation and the system overview.
4. **B1 — alpha onboarding:** organization creation, owner invitation, customer search/card, alpha grant/renew/revoke. This is the first operator-console acceptance milestone.
5. **B4 — operations and support:** audit/error-event and traceId search plus metadata-only service heartbeats and the `/admin/operations` overview are implemented. Container-log access through a restricted external ops boundary remains future work.
6. **B2/B3 — commercial conversion:** the provider-neutral operator plan catalog at `/admin/billing/plans`, tenant-scoped subscription persistence, append-only billing ledger, operator-only manual invoice lifecycle and atomic/idempotent Alpha → Production conversion are implemented. Server-generated PDF invoice generation/delivery, provider-backed funds verification/reconciliation, granular entitlement/usage enforcement and provider adapters remain future work. The customer `/billing` page and browser print view expose only current-organization invoices to Owners/Admins.
7. **B5 — provider integration:** choose a provider after product/commercial/legal decisions; implement the adapter, signed webhook and reconciliation.
8. **B6 — data lifecycle:** export and deletion workflows, retention and operator correction reporting.

The first customer-alpha acceptance criterion is operational, not architectural: an operator can create an organization, invite its owner, grant access, inspect metadata and support the customer without editing the database manually. The current B1 slice provides the organization list/search, customer detail, organization creation with owner invitation, additional owner invitations, invitation revocation, member/invitation visibility, a read-only project inventory, a safe workstation inventory for operators with `UsersRead`, and Alpha grant/renew/revoke with limits, features, effective dates and audit history. Owner/member identity details are withheld unless the actor has `UsersRead`; counts remain visible without exposing personal data. Expired invitations are not counted as pending. Full account operations and entitlement enforcement beyond the atomic `projects` and `members` capacity caps remain subsequent work. High-risk operator mutations now require signed ten-minute TOTP step-up elevation and are checked again in the server-side services.

## Current implementation status of this foundation

Implemented in the foundation branch:

- Operator permission flags, reserved SystemAdministrator/Support/Billing/Auditor role keys and permission sets, an immutable AdminActor contract, database-backed IAdminGate authorization, TOTP enrollment at `/control/security`, one-time SHA-256-hashed recovery codes with rotation and recovery for every operator role, and signed ten-minute step-up elevation for high-risk mutations.
- An isolated Control Plane origin (`control.projectbeacon.tech`) with a host allowlist, separate host-only operator cookie, dedicated sign-in, and Control Plane-only bootstrap/recovery/audit routes.
- PostgreSQL-backed Alpha entitlements with tenant/operator RLS, atomic replace/grant/revoke operations and transactional audit records; Alpha → Production conversion is implemented in `PostgresEntitlementService` as described below.
- Provider-neutral billing contracts (`IBillingProvider`, resolver, checkout/webhook and payment-method-reference ports) are defined, but no provider adapter is registered or selected. Persistence exists for plans, subscriptions, manual invoices and append-only ledger entries; payment-method-reference persistence remains future work.
- Registration mode/policy contracts.
- Invite-only registration enforced as the current runtime policy.
- Operator organization management: paginated/searchable customer list, organization detail, member/invitation metadata, organization creation with owner invitation, invite reissue/revocation, member-role changes, and global account disable/enable with owner-protection and session invalidation.
- Organization lifecycle transitions (Active/Suspended/Closed) with a reason, TOTP step-up, row locking and atomic audit records. Suspended and closed tenants are blocked across API entitlement checks, ordinary EF tenant filters, shared Application authorization checks, and invite preview/redemption. Existing global user sessions remain valid; a user can switch to another active organization. Permanent closure is terminal and reserved for SystemAdministrators.
- Persisted organization entitlements with operator list/grant/renew/revoke, replacement history, transactionally staged audit events, tenant RLS and an active-grant uniqueness constraint.
- First-party Web/API/Worker processes publish metadata-only heartbeats every 30 seconds. The `/admin/operations` overview is permission-gated by `OperationsRead` and marks instances stale after 90 seconds; it exposes no hostnames or log content.
- The provider-neutral `BillingPlan` catalog is persisted in `BillingPlans` under FORCE RLS, with `/admin/billing/plans` gated by `BillingRead` for reads and `BillingManage` plus TOTP step-up for mutations. Price and currency are optional; the catalog does not create subscriptions or contact a payment provider.
- `OrganizationSubscriptions` stores provider-neutral subscription status/source, plan key and period with one non-terminal subscription per organization; tenant and operator reads/writes are enforced by FORCE RLS.
- `BillingLedgerEntries` is tenant/operator FORCE-RLS protected and database append-only. Provider event IDs and idempotency keys are unique; a composite FK prevents linking a ledger row to a subscription belonging to another organization.
- Alpha → Production conversion is implemented in `PostgresEntitlementService`: a persisted operator permission plus TOTP step-up, active-org locking, exact idempotent replay, plan-feature/limit copy, atomic Alpha grant replacement, ManualInvoice subscription creation, ledger activation event and three audit records in one transaction. PostgreSQL runtime-role integration tests cover the conversion and replay.
- The conversion stores an operator-supplied manual invoice/receipt reference as part of the idempotent conversion record; it does not itself prove that funds were received or issue an invoice. Separately, `/admin/billing/invoices` provides numbered manual invoice issuance and Open/Paid/Void/Uncollectible lifecycle management. Marking an invoice paid stores an external receipt reference but does not verify receipt of funds or perform reconciliation. `/billing` exposes invoices to the active organization's Owner/Admin through a tenant-bound read-only service; ordinary members and inactive tenants are denied.
- Sensitive organization, user and entitlement mutations require TOTP step-up and are independently checked by the Application services.
- SaaS-only product boundary documented in README and AGENTS.

Not implemented by this foundation:

- A separate operator identity/credential store is deferred; operators currently reuse customer `User` accounts with persisted role keys. Role assignment and removal are implemented through `/admin/operators`. All operator roles can rotate one-time recovery codes from `/control/security` while their authenticator is available and use a saved code to disable a lost authenticator; recovery consumes one code, revokes the remaining codes and invalidates active sessions. The primary SystemAdministrator also retains deployment-token-protected break-glass recovery.
- Tenant-scoped API requests now fail closed unless the organization has an active entitlement containing `core`; this applies to human, API-token and device requests. Login, password recovery, and invitation redemption remain available so access can be restored. The product-owned `projects` capacity key is enforced atomically when creating projects. The `members` key reserves organization members plus valid org invitations and is checked when org/project invites are accepted. An omitted key is unmetered and an explicit zero blocks new usage. Other module feature gates and usage counters remain future work.
- Server-generated PDF artifact generation and delivery; payment-method-reference persistence; payment-provider adapters, checkout/portal, verified webhooks, funds verification, refunds and automated settlement reconciliation. The browser print/Save PDF view is implemented as an informational copy of the manual record, not a tax/fiscal invoice. Manual invoice state transitions and operator-attested receipt references are implemented, but they do not prove funds were received. The `/billing` customer view is read-only and limited to organization Owners/Admins. Alpha-to-production conversion is already implemented as described above; provider-backed renewal and lifecycle flows remain future work.
- Approval-request and verified-email registration handlers.
- Production deployment of an ops-agent or external log stack.

Do not treat a contract interface as a shipped feature. Each later phase must add persistence, DI registration, UI/API entry points, authorization tests, PostgreSQL/RLS tests and operational documentation together.
