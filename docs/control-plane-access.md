# ProjectBeacon Control Plane access boundary

## Purpose

The Control Plane is the administrative surface for operating the hosted ProjectBeacon SaaS. It
must not be a section of the customer application's route tree or share the customer application's
browser session.

The initial deployment uses a dedicated origin:

- Customer application: the existing public Web hostname.
- Operator console: `control.projectbeacon.tech`.
- Public API: the separate `ProjectBeacon.API` host, when deployed independently.

The Web binary and PostgreSQL database are shared in this first stage, but the origin, route allowlist,
authentication scheme, and browser session are separate. A later deployment can run the Control
Plane in its own Web deployment without changing the application-level audit contracts.

## Host boundary

`CONTROL_HOST` (or `Control:Host`) identifies the exact operator hostname. The production default is
`control.projectbeacon.tech`; local development should set `CONTROL_HOST=control.localhost`.

`ControlHostMiddleware` runs in the Web host before static-file handling and endpoint routing:

- On the customer hostname, `/admin/*`, `/control/*`, `/bootstrap`, `/recover`, `/v1/admin/*`,
  `/v1/auth/control-cookie-login`, `/v1/auth/bootstrap`, and
  `/v1/auth/recover-admin` return `404`.
- On the control hostname, only the Control Plane pages and supporting routes are allowed:
  `/control/login`, `/control/bootstrap`, `/control/recover`, `/control/logout`,
  `/control/security`, `/control/elevate`, `/admin/audit`, `/admin/operations`, `/admin/billing/plans`, `/admin/billing/invoices`, `/admin/email`, `/admin/operators`, `/admin/organizations` (including organization detail routes), the audit read/export endpoints, the control-login endpoint, health/culture,
  and static/Blazor framework assets. Customer application pages are not served from this origin.
- The control hostname root redirects to `/admin/audit`; an unauthenticated browser is challenged
  to `/control/login`.
- The standalone `ProjectBeacon.API` host has a separate guard which always returns `404` for
  operator, bootstrap, and break-glass recovery routes. The Control Plane's server-side audit API
  is served by the Web host on the control origin, not by the public API deployment.

This boundary is enforced by the application and does not rely only on DNS or ingress routing.
DNS and TLS must still be configured so that the control hostname reaches the Web host.

## Separate authentication

The Control Plane does not accept the customer application's cookie or a customer JWT as its
operator session.

- Customer sign-in remains on `/login` and issues the host-only `BeaconAuth` cookie.
- Control Plane sign-in is `/control/login`, submitted to
  `POST /v1/auth/control-cookie-login`.
- The endpoint verifies the credentials using the shared account service and issues a separate
  host-only `BeaconControlAuth` cookie only when the persisted account has one of the valid
  platform-operator roles. Customer accounts without an operator role are refused.
- The control cookie is HttpOnly, SameSite=Strict, Secure in Production, and has an eight-hour
  absolute ticket lifetime. No cookie Domain attribute is set, so the browser does not send it
  to the customer hostname.
- Control sign-in and sign-out POSTs require a form content type and an exact same-origin
  `Origin` match; when Fetch Metadata is present, `Sec-Fetch-Site` must be `same-origin`.
  Same-site sibling subdomains are rejected. Sign-in is rate-limited; sign-out requires
  an authenticated Control Plane session and records `platform.control_logout.succeeded`.
- On the control hostname, the default authentication selector uses only the Control Plane cookie
  scheme; customer JWTs and the customer cookie are not alternate authentication paths.
- The audit page is explicitly bound to the Control Plane cookie scheme. Its Blazor component also
  checks the current origin and the `control_plane` session claim before calling any audit service;
  this protects against client-side navigation inside an already-open customer circuit. The audit
  API independently rechecks the current persisted operator authority using `IAdminGate` on every request.
- The control host serves only the explicit shared shell asset allowlist (`/_framework/*`, `/_content/*`,
  `/_blazor/*`, `/brand/*`, `/app.css` and its seven imported `app-*.css` stylesheets,
  `/shell.js`, `/timezone.js`, `/chat.js`, and `/favicon.ico`).

The Control Plane reuses the existing `User` account and credentials; a separate operator identity
and credential store is deferred. Operator roles are persisted as a key on the user account and can
be assigned or removed at `/admin/operators` by an elevated SystemAdministrator. The four roles
(`SystemAdministrator`, `Support`, `Billing`, and `Auditor`) are resolved by `IAdminGate` on every
privileged operation. The Control Plane ticket marks an authenticated operator, while the legacy
`User.IsAdmin` flag remains true only for SystemAdministrator and preserves customer-side admin
semantics. Role changes increment `SessionVersion`, so older sessions cannot retain their previous
permissions. Role changes require a fresh TOTP step-up and are audited.

## Bootstrap and recovery

The initial administrator bootstrap and break-glass password recovery are Control Plane functions:

- `/control/bootstrap` calls `POST /v1/auth/bootstrap`.
- `/control/recover` calls `POST /v1/auth/recover-admin`.

Both are unavailable on the customer and standalone API hosts. Bootstrap and recovery pages also
check the current browser origin and recheck it immediately before submitting their action. The
recovery handler records denied, failed, and successful attempts without storing the bootstrap token or generated password. A successful break-glass recovery selects only an active SystemAdministrator, resets the password, clears enabled or pending TOTP enrollment, increments SessionVersion, and invalidates the local session-version cache. If an authenticator was cleared, a dedicated audit event is committed with the recovery event. The generated password is returned once; the operator must sign in and enroll a new authenticator before using elevated actions.
Bootstrap remains protected by the configured secret and rate limit; it can create the first
administrator only.

## Deployment

For the single-VPS Docker Compose deployment:

1. Set `CONTROL_HOST=control.projectbeacon.tech` in `deploy/compose/.env`.
2. Create a DNS A/AAAA record for the control hostname pointing to the same VPS as `BEACON_DOMAIN`,
   or a CNAME from `control.projectbeacon.tech` to `BEACON_DOMAIN`.
3. The production Caddyfile serves both `BEACON_DOMAIN` and `CONTROL_HOST`. Caddy obtains and renews
   Let's Encrypt certificates covering both names automatically; keep ports 80 and 443 reachable.
4. The Web container and Caddy receive the same `CONTROL_HOST` value.

For the Kubernetes deployment:

1. Set `CONTROL_HOST=control.projectbeacon.tech` on every Web color/deployment.
2. Create a DNS A/AAAA or CNAME record for `control.projectbeacon.tech` pointing to the same ingress
   entry point as the customer Web host.
3. Add `control.projectbeacon.tech` as an ingress host routed to the Web service. Provision the
   referenced `beacon-web-tls` secret with a certificate covering both customer and control hostnames;
   the repo does not assume a particular cert-manager issuer.
4. Configure trusted forwarded headers on **both** Web colors. TLS terminates at the ingress, so
   `FORWARDEDHEADERS__KNOWNNETWORKS__0` must include the actual ingress-controller pod CIDR (or
   configure its exact pod IPs with `FORWARDEDHEADERS__KNOWNPROXIES__0`). Without this, the
   Control Plane exact-origin guard sees the upstream HTTP scheme and rejects browser login/logout
   with `403`. See the TLS termination section in `deploy/README.md`; do not trust all proxy
   sources.

For either deployment, keep the standalone API service on its public API hostname; it never serves
operator routes. Smoke-test that `/login` and `/dashboard` work on the customer host, while
`/admin/audit`, `/control/login`, `/bootstrap`, and `/recover` return `404` there. Verify that
`/control/login` works only on the control host and that a non-operator account is refused.

For local development, add `CONTROL_HOST=control.localhost` to `.env`. Browse the customer app at
`http://localhost:5083` and the Control Plane at `http://control.localhost:5083/control/login`.
Run the first-admin bootstrap at `http://control.localhost:5083/control/bootstrap`.

## Explicitly not complete yet

- Operator identities do not yet have a separate credential/account store; they reuse customer
  `User` accounts. Persisted SystemAdministrator/Support/Billing/Auditor roles can be assigned
  and removed at `/admin/operators`. The role is stored on the user account, and the legacy
  `User.IsAdmin` marker is true only for SystemAdministrator; `IAdminGate` derives the effective
  permissions from the persisted role.
- Operator TOTP enrollment is available at `/control/security`. A successful `/control/elevate`
  verification places a signed ten-minute elevation claim in the Control Plane cookie. Organization
  creation, owner invitations, invitation revocation, Alpha entitlement changes, operator role changes,
  and SMTP mutations require this elevation and recheck it in the server-side service/gate path.
  Operators without TOTP enabled cannot perform these sensitive changes. Every active operator
  role receives one-time recovery codes during authenticator setup; a saved code at
  `/control/security` disables the lost authenticator, consumes that code, revokes all remaining
  recovery codes, increments the session version and invalidates active sessions. The operator
  must sign in again and enroll a new authenticator. The active SystemAdministrator additionally
  retains the deployment-token-protected `/control/recover` break-glass path.
- Organization/customer onboarding and Alpha entitlement management are available under
  `/admin/organizations`: list/search, customer detail, organization creation with owner invitation,
  member/invitation visibility, owner invitation creation/revocation, member-role changes, global
  customer-account enable/disable, Alpha grant/renew/revoke, and organization lifecycle controls.
  Organizations can move Active → Suspended → Active or Suspended → Closed; Closed is terminal,
  retains customer data, and is restricted to SystemAdministrators. Lifecycle transitions require
  TOTP step-up, a reason and an atomic audit event. Suspended/closed organizations are denied by
  API entitlement checks, ordinary tenant EF filters, shared Application authorization checks, and
  invite preview/redemption checks. Existing circuits retain their global user session but their
  inactive tenant data stays blocked; a customer with another active membership can switch to it.
  Account disable is global across every organization the user belongs to, increments the session
  version, invalidates the local session-version cache, and refuses to disable an owner unless every
  affected organization has another active owner. The last active owner also cannot be demoted.
- This stage isolates the host and browser session but uses the same Web process/image and database.
  A separate Control Plane deployment remains a future hardening option.
- Metadata-only Web/API/Worker heartbeats are written every 30 seconds and visible to operators
  with `OperationsRead` at `/admin/operations`; instances become stale after 90 seconds without an
  update. Container log access is not exposed: it still requires a restricted external ops-agent
  or log stack and the `ContainerLogsRead` boundary.
- The provider-neutral plan catalog is available at `/admin/billing/plans`. `BillingRead` operators
  can inspect plans; create/update operations require `BillingManage` and ten-minute TOTP step-up.
  Plan price and currency may both remain unset. The catalog does not create subscriptions or call
  a payment provider.
- Billing persistence/provider integration, data export/deletion, and granular feature/usage
  entitlement enforcement remain future work.
