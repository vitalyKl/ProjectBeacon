# ProjectBeacon SaaS operator foundation

**Status:** foundation contracts and SaaS registration defaults. This document does not claim that the operator UI, billing persistence, provider adapters, or access enforcement are implemented.

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

Alpha and Production both resolve to the same effective entitlement contract. The Application contract exposes `IEntitlementService.ConvertAlphaToProductionAsync` as the one-action transition. It replaces the active AlphaProgram grant with a Subscription-backed grant; it is not an organization migration. The implementation must atomically persist the subscription state and billing event, close the alpha grant, create the production entitlement, and stage the operator audit entry in one database transaction. Failed transitions must leave the existing alpha access unchanged. The operation must be idempotent on its request key so a retry does not double-activate or double-charge.

## Operator authorization contract

Application contracts live in ProjectBeacon.Application/Admin:

- OperatorPermission is a set of named capabilities, not an operator role or a claim.
- AdminActor is the verified operator identity passed to privileged handlers. Its constructor is internal; callers obtain it only from IAdminGate.
- IAdminGate is responsible for resolving a human actor against current database state and checking requested permissions and elevation.

For the alpha, the existing User.IsAdmin flag maps to OperatorPermissionSets.CurrentSystemAdministrator. No Support, Billing, or Auditor role is persisted or assignable yet.

Future operator roles should be mapped to permission sets in one authorization component. Handlers must continue asking for a permission, not checking a role name or User.IsAdmin directly. Project MemberRole and ApiTokenCapability remain unrelated security domains. API tokens, devices, and workers never receive operator permissions.

Sensitive mutations require an elevated AdminActor. The future gate must validate the current user and session version in the database on each invocation; a Blazor page authorization attribute is only a UI guard.

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

- IBillingProvider defines checkout, customer portal, subscription retrieval/change/cancellation, and verified webhook normalization.
- IBillingProviderResolver selects an adapter by an application-owned provider key.
- BillingProviderEvent is a normalized event with an external event ID for idempotency. Provider SDK objects and raw webhook payloads do not cross the adapter boundary.
- BillingCheckoutRequest refers to a stable PlanKey, not a provider-specific price ID.

No provider is selected or registered for the alpha. There is no provider SDK dependency in Domain or Application and no provider-specific endpoint yet. Selecting a provider later adds an Infrastructure adapter and configuration, not changes to handlers or the entitlement contract.

Payment collection should use provider-hosted checkout/portal and payment-method setup sessions. `IBillingProvider` reserves setup, list, set-default and detach operations; the browser collects payment details directly with the provider. ProjectBeacon must never receive or persist full card numbers, CVV/CVC, magnetic-stripe data, bank login credentials, or equivalent raw payment credentials.

IPaymentMethodReferenceStore is reserved for opaque provider-owned references and masked presentation metadata only (brand, last four digits, expiry). A provider reference must be encrypted at rest if its provider treats it as a credential. Provider secrets and webhook-signing secrets belong in deployment secret management, never in organization rows, application settings committed to source, audit records, or logs.

Billing events are append-only and idempotent on (ProviderKey, ExternalEventId). Monetary amounts use integer minor units and ISO 4217 currency codes. Refunds are reflected as verified events; the operator console must not invent a money-moving refund API.

## Registration contract

Application contracts live in ProjectBeacon.Application/Auth/RegistrationPolicy.cs:

- InviteOnly is the only active SaaS alpha mode.
- ApprovalRequired reserves a flow in which a request remains pending until an operator approves it.
- OpenWithVerifiedEmail reserves future self-service registration and must require verified email before an account or organization is activated.

Only InviteOnly is implemented in RegisterHandler today. The runtime registration guard is fail-closed and does not enable public sign-up through AUTH_LOCAL_INVITE_ONLY. The enum values for future modes are contracts only; do not expose them as configuration options until their complete handlers, email verification, rate limits, abuse controls, tenant-creation rules, audit events, and tests exist.

The alpha flow is operator-created organization → organization-owner invitation → invited owner registration. Creating a user without a valid invitation is not an alpha onboarding path.

## Data and extension points for later phases

The following persistence models are not created by this contract-only change. When implementing billing, add them with EF configurations, migrations, RLS/grants, and PostgreSQL tests:

| Future model | Responsibility | Required invariant |
|---|---|---|
| OrganizationEntitlements | Versioned access grants for an organization | Source, status and effective period are explicit; no implicit access from Org existence |
| Plan | Product-owned plan key, public name and product limits | Prices and provider price IDs are external/configurable |
| Subscription | Commercial state and billing period | At most one effective subscription per organization |
| BillingEvent | Verified payment/subscription facts | Append-only and provider-event idempotency |
| Invoice | Manual invoice and payment reference | Amount/currency/date/status are explicit; no payment credential data |
| PaymentMethodReference | Provider reference and masked metadata | No PAN/CVV or provider secret token |

RLS must scope all organization-owned billing and entitlement rows by OrgId. Operator-wide reads must use the reviewed admin data-access boundary, not switch the current Blazor tenant. Audit writes for operator mutations and billing state transitions must be atomic with the corresponding database change.

## Delivery order

1. **A0 — access revocation:** DisabledAt, SessionVersion, cookie/JWT validation and circuit revalidation.
2. **A1 — audit core:** append-only AuditEvents, transactional Stage writes, redaction and security-event coverage.
3. **A2 — operator gate:** database-backed IAdminGate, AdminActor creation, TOTP/elevation, admin-only navigation and the system overview.
4. **B1 — alpha onboarding:** organization creation, owner invitation, customer search/card, alpha grant/renew/revoke. This is the first operator-console acceptance milestone.
5. **B4 — operations and support:** error events, service heartbeats, container-log access through a restricted external ops boundary, and traceId search.
6. **B2/B3 — commercial conversion:** plans, subscriptions, invoices, billing event ledger, entitlement enforcement, owner billing page and a one-action alpha-to-production transition.
7. **B5 — provider integration:** choose a provider after product/commercial/legal decisions; implement the adapter, signed webhook and reconciliation.
8. **B6 — data lifecycle:** export and deletion workflows, retention and operator correction reporting.

The first customer-alpha acceptance criterion is operational, not architectural: an operator can create an organization, invite its owner, grant access, inspect metadata and support the customer without editing the database manually. Every privileged mutation is authorized, elevated where required, and audited.

## Current implementation status of this foundation

Implemented in the foundation branch:

- Operator permission flags, reserved SystemAdministrator/Support/Billing/Auditor role keys and permission sets, an immutable AdminActor contract, and IAdminGate port.
- Entitlement source/status/limit/snapshot contracts and IEntitlementService port.
- Provider-neutral billing request/event/response contracts, hosted payment-method setup/lifecycle, IBillingProvider, resolver and payment-reference store port.
- Registration mode/policy contracts.
- Invite-only registration enforced as the current runtime policy.
- SaaS-only product boundary documented in README and AGENTS.

Not implemented by this foundation:

- AdminGate implementation, access revocation, audit storage, elevation/TOTP flow, Admin UI, organization creation or alpha-grant persistence.
- Any billing database table, provider adapter, checkout, webhook route, payment-method storage implementation or customer billing UI.
- Approval-request and verified-email registration handlers.
- Production deployment of an ops-agent or external log stack.

Do not treat a contract interface as a shipped feature. Each later phase must add persistence, DI registration, UI/API entry points, authorization tests, PostgreSQL/RLS tests and operational documentation together.
