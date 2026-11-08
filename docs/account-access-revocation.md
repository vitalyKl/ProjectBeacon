# Account access revocation (A0)

This document describes the first executable security phase of the SaaS operator console.

## State model

`Users` now has:

- `DisabledAt`: nullable UTC timestamp. A disabled account cannot sign in or retain an authenticated cookie/JWT.
- `SessionVersion`: non-negative integer starting at zero. Every global credential-revocation event increments it.

The `User` aggregate exposes `Disable`, `Enable`, `SetAdmin`, and `BumpSessionVersion`. These operations are idempotent where applicable and increment the version only when state changes. Password replacement increments it as well.

## Ticket issuance and validation

Cookie login and JWT issuance both include a signed `sv` claim. The existing `isAdmin` claim remains present for current authorization compatibility.

Every cookie and JWT authentication pipeline validates:
- subject identifier is a non-empty user id;
- `sv` is a valid non-negative integer (legacy tickets without `sv` are interpreted as version zero);
- the account exists and is not disabled;
- persisted session version matches the ticket;
- persisted admin status matches the ticket's `isAdmin` claim.

The check uses `IUserSessionVersionCache` with a 30-second per-process TTL. A session-version or admin-claim mismatch forces an immediate database refresh, so newly issued tickets work immediately after a security change. Other service instances refresh when their short cache entry expires. The maximum stale-ticket window from an operator mutation is therefore bounded by the cache TTL, including across Web replicas. In-process invalidation can be called by future operator handlers.

Interactive Server circuits run the same check during existing 60-second membership revalidation and reconnect validation; invalid state signs the circuit out.

## Revocation events

The following operations increment `SessionVersion`:
- password change and password reset;
- account disable and enable;
- system-administrator grant or removal;
- explicit forced logout;
- break-glass admin recovery (through the same password-reset aggregate operation).

API logout increments the version and deactivates active `UserSession` history rows instead of deleting them. `UserSession` remains a login history record and is not the authentication authority.

Login for a disabled account returns the same generic invalid-credentials response used for an unknown account or wrong password.

## Database migration and verification

Migration `20261108130000_AddUserAccessState` adds `DisabledAt` and `SessionVersion` with a zero default for existing users. EF model configuration and snapshot are updated together.

Coverage includes domain idempotence/version increments, disabled-user login rejection, circuit revalidation, and validator behavior for disabled accounts, stale versions, and admin-claim changes.

## Operational note

The cache is deliberately short-lived and local to each Web/API process. This avoids a database read on every request while bounding revocation propagation across replicas. If future requirements demand revocation faster than 30 seconds across replicas, replace the cache implementation with shared distributed invalidation without changing the Application port.
