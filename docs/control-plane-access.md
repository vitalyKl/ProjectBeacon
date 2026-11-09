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

`ControlHostMiddleware` runs in the Web host after routing and authentication:

- On the customer hostname, `/admin/*`, `/control/*`, `/bootstrap`, `/recover`, `/v1/admin/*`,
  `/v1/auth/control-cookie-login`, `/v1/auth/bootstrap`, and
  `/v1/auth/recover-admin` return `404`.
- On the control hostname, only the Control Plane pages and supporting routes are allowed:
  `/control/login`, `/control/bootstrap`, `/control/recover`, `/control/logout`,
  `/admin/audit`, the audit read/export endpoints, the control-login endpoint, health/culture,
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
  host-only `BeaconControlAuth` cookie only when the persisted account is a platform administrator.
- The control cookie is HttpOnly, SameSite=Strict, Secure in Production, and has an eight-hour
  absolute ticket lifetime. No cookie Domain attribute is set, so the browser does not send it
  to the customer hostname.
- Control sign-in and sign-out POSTs require a form content type and an exact same-origin
  `Origin` match; when Fetch Metadata is present, `Sec-Fetch-Site` must be `same-origin`.
  Same-site sibling subdomains are rejected. Sign-in is also rate-limited; sign-out requires
  an authenticated Control Plane session.
- On the control hostname, the default authentication selector uses only the Control Plane cookie
  scheme; customer JWTs and the customer cookie are not alternate authentication paths.
- The audit page is explicitly bound to the Control Plane cookie scheme. The audit API independently
  rechecks the current persisted operator authority using `IAdminGate` on every request.

The first slice deliberately reuses the existing `User` account and `User.IsAdmin` flag as the
operator eligibility source. This is separate authentication and session handling, not yet a
separate operator credential store or a persisted multi-role operator directory. The next
authorization phase should introduce assignable operator roles and mandatory MFA/elevation without
reintroducing customer-domain access.

## Bootstrap and recovery

The initial administrator bootstrap and break-glass password recovery are Control Plane functions:

- `/control/bootstrap` calls `POST /v1/auth/bootstrap`.
- `/control/recover` calls `POST /v1/auth/recover-admin`.

Both are unavailable on the customer and standalone API hosts. The recovery handler records denied,
failed, and successful attempts without storing the bootstrap token or generated password.
A successful password reset and its audit event are staged in the same database unit of work.
Bootstrap remains protected by the configured secret and rate limit; it can create the first
administrator only.

## Deployment

For Production:

1. Set `CONTROL_HOST=control.projectbeacon.tech` on every Web color/deployment.
2. Create a DNS A/AAAA or CNAME record for `control.projectbeacon.tech` pointing to the same ingress
   entry point as the customer Web host.
3. Add `control.projectbeacon.tech` as an ingress host routed to the Web service and include it in
   the TLS certificate.
4. Keep the standalone API service on its public API hostname; it never serves operator routes.
5. Smoke-test that `/login` and `/dashboard` work on the customer host, while
   `/admin/audit`, `/control/login`, `/bootstrap`, and `/recover` return `404` there. Verify
   that `/control/login` works only on the control host and that a non-operator account is refused.

For local development, add `CONTROL_HOST=control.localhost` to `.env`. Browse the customer app at
`http://localhost:5083` and the Control Plane at `http://control.localhost:5083/control/login`.
Run the first-admin bootstrap at `http://control.localhost:5083/control/bootstrap`.

## Explicitly not complete yet

- Operator identities and assignable SystemAdministrator/Support/Billing/Auditor role membership
  are not separate persisted records yet; current admission uses `User.IsAdmin`.
- TOTP/elevation must be completed before sensitive actions are enabled. The operator gate remains
  fail-closed for actions that request elevation.
- This stage isolates the host and browser session but uses the same Web process/image and database.
  A separate Control Plane deployment remains a future hardening option.
- The first UI page on the Control Plane is the audit console. Organization/customer management,
  subscriptions, entitlements, licensing, and operational controls are separate implementation work.
