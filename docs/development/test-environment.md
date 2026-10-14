# ProjectBeacon test environment (canonical)

One reference for running the Web app locally and executing the three test execution paths: unit tests, PostgreSQL integration tests, and Playwright E2E. Every command and port below was verified against the current checkout on 2026-10-14; items that are read from configuration but not yet executed locally are marked **(configuration-derived, unverified)**.

## 1. Prerequisites

| Requirement | Value / source |
|---|---|
| .NET SDK | 10.0.401, `rollForward: latestPatch` (`global.json`); all projects target `net9.0`. |
| Docker | Required for the `beacon-postgres` container and for Testcontainers in `ProjectBeacon.API.Tests` (unless `BEACON_TEST_DATABASE_URL` points at a disposable PostgreSQL instance). |
| Node.js + npm | Playwright suite: `tests/playwright/package.json` (`@playwright/test ^1.63.0`); `node_modules/` is checked in as installed — run `npm ci` in `tests/playwright` only if it is missing. Browser binaries: `npx playwright install chromium` **only if** the suite fails to launch a browser (already present in this environment; the command itself is unverified here). |
| `.env` at the repo root | Copy `.env.example` → `.env`; set `POSTGRES_PASSWORD`, `ConnectionStrings__Default`, `BOOTSTRAP_ADMIN_TOKEN`, `JWT__Secret`. `AUTH_LOCAL_INVITE_ONLY=true` is the sample default (registration requires an invite). Never commit `.env`; never print its values. |

Environment precedence: process environment variables always win. `ProjectBeacon.Web` calls `EnvFile.Load()` (`ProjectBeacon.Infrastructure/Data/EnvFile.cs`), which walks up from the working directory / app base, loads the first `.env` it finds, and only sets variables that are not already set.

## 2. PostgreSQL (shared development database)

- Command: `docker compose up -d` from the repo root (`docker-compose.yml`).
- Container `beacon-postgres`, image `postgres:16-bookworm`, named volume `pgdata`, host port **5432** → container 5432.
- Database `beacon`; superuser `beacon` (password from `POSTGRES_PASSWORD`). Healthcheck: `pg_isready -U beacon -d beacon`.
- Two roles (see `ProjectBeacon.Infrastructure/Data/PostgresConnection.cs`):
  - `beacon` — superuser; applies EF migrations, bootstraps the runtime role (`EnsureAppRoleAsync` is idempotent and runs at Web startup).
  - `beacon_app` — non-superuser, `NOSUPERUSER NOBYPASSRLS`; the role the application and its query filters/RLS run as.
- The Web host in `Development` also auto-starts this container itself (`ProjectBeacon.Web/Startup/DockerPostgresHelper.cs`): it reuses a running container, starts one only if absent, and waits up to 60s for healthy.
- The Web host migrates on startup unless the environment is `Production` or `BEACON_MIGRATE_ON_START=false` (`ProjectBeacon.Web/Program.cs`). `BEACON_MIGRATE_THEN_EXIT=true` migrates and exits (used by the k8s migrate Job).

This database is **shared state**. Never `DROP` it, never recreate the container or the `pgdata` volume, and never point `BEACON_TEST_DATABASE_URL` at it (see §4).

## 3. Web application: build, start, readiness

### Check before starting

```
netstat -ano | findstr ":5083"        # existing Web listener? (5183 / 7118 for the other profiles)
docker ps --filter name=beacon-postgres
```

Reuse a healthy existing instance. Only start a new one if the port is free, and record the PID/process you start so you can stop exactly that one.

### Canonical start (Development)

```
dotnet run --project ProjectBeacon.Web --launch-profile http
```

- Listens on **http://localhost:5083** (`ProjectBeacon.Web/Properties/launchSettings.json`), `ASPNETCORE_ENVIRONMENT=Development`.
- Launches a browser (`launchBrowser: true`) — harmless, but note it.
- Other profiles in the same file:
  - `https` → `https://localhost:7118` + `http://localhost:5083`.
  - `http-test` → `http://localhost:5183` with `ConnectionStrings__Default` overridden to `Database=beacon_test`. **The `beacon_test` database is not created by any code in the repo** (checked 2026-10-14: live cluster contains only `beacon`, `beacon_tenant_rebind`, `postgres`). Create it manually as superuser first, or don't use this profile. **(configuration-derived, unverified)**

### Alternative: run the built DLL (used for the P1.5 E2E run, verified working)

```
dotnet build ProjectBeacon.Web
dotnet ProjectBeacon.Web/bin/Debug/net9.0/ProjectBeacon.Web.dll --urls "http://localhost:5083"
```

Run it **from the repo root** so `EnvFile.Load()` finds `.env` (the connection string then resolves from `POSTGRES_PASSWORD`). Caveat: with CWD=repo root the content root is the repo root, so `ProjectBeacon.Web/appsettings.json` is **not** loaded — its `Logging:LogLevel` settings are inert in that mode (observed: framework `Information` logs stream to the console and `Microsoft.AspNetCore: Warning` is not applied). Prefer `dotnet run` when you need the appsettings logging configuration.

Always build the current code before starting; never assume an existing DLL contains uncommitted changes.

### Readiness and startup-failure diagnosis

- Console shows `Now listening on: http://localhost:5083`.
- `curl -I http://localhost:5083/login` → HTTP 200.
- `GET /v1/version` → `{ "version": "0.5.0", "gitSha": "..." }` (version from `Directory.Build.props`; `gitSha` from `BEACON_GIT_SHA`).
- Startup failure signals in the Web log:
  - `Docker PostgreSQL helper did not start a container...` — Docker missing/daemon down; the app then tries to migrate against whatever `ConnectionStrings:Default` points at.
  - `Set ConnectionStrings:Default (with Password) or POSTGRES_PASSWORD...` — no `.env` reachable from the CWD and no env vars set (`PostgresConnection.Resolve` throws).
  - `The migration connection string has no password...` — `ConnectionStrings__Migration` used without a password.
- First login: `/bootstrap` with `BOOTSTRAP_ADMIN_TOKEN`, then `/login`. With `AUTH_LOCAL_INVITE_ONLY=true`, `/register` requires an invite.

## 4. Test execution paths

Treat these three paths as separate: each has its own prerequisites and its own definition of a real run. Record for every run: exact command, working directory, exit code, test counts, and log path.

### A. Unit tests (no Postgres, no ports)

```
dotnet build ProjectBeacon.sln
dotnet test ProjectBeacon.Web.Tests
```

- `ProjectBeacon.Web.Tests` (xUnit + bUnit + `Microsoft.AspNetCore.Mvc.Testing`, SQLite in-memory for DB-dependent cases) needs no services. Verified 2026-10-14: **115/115 passed, 8s**.
- Solution-wide run uses the GPU-lane filter encoded in `test.runsettings` (`Lane!=Gpu`); CI runs `dotnet test ProjectBeacon.sln --no-build --verbosity normal --filter "Lane!=Gpu"`.
- `dotnet format ProjectBeacon.sln --verify-no-changes` is the style gate (CI step).

### B. PostgreSQL integration tests (`ProjectBeacon.API.Tests`)

```
dotnet test ProjectBeacon.API.Tests
```

Fixture behavior (`ProjectBeacon.API.Tests/PostgresFixture.cs`), in order:

1. `BEACON_TEST_SKIP_POSTGRES` set to `1`/`true` → every PostgreSQL-dependent test is **explicitly skipped** (status `Skipped`, with the reason). This is the only sanctioned skip path.
2. Else `BEACON_TEST_DATABASE_URL` set → that URL is used (converted `postgres://` → Npgsql key=value). No Testcontainers.
3. Else → Testcontainers `postgres:16-alpine` (db `beacon`, user/password `beacon` — constants in the fixture, not the shared cluster), `EnsureCreatedAsync`, then idempotent `EnsureAppRoleAsync`.
4. Else (no URL and Testcontainers failed) → every PostgreSQL-dependent test **fails** with a diagnostic naming the dependency, the attempted mechanism, the last error, and the corrective action. An infrastructure failure is never reported as a pass.

Every PostgreSQL-dependent test calls `PostgresFixture.RequirePostgres()` first; there is no silent early-return path. A fixture-based test reported as passed means its assertions executed.

**Destructive / shared-state warning:** `MigrationApplyTests` (fixture-based) **drops and recreates** the `beacon_migrate_test` database on the fixture's server — a disposable Testcontainers container by default, or the server of an explicitly supplied `BEACON_TEST_DATABASE_URL`. Pointing the URL at the shared development or production server is how a bad test run can destroy shared state — never do it to work around a Testcontainers failure. If Testcontainers is unavailable, the honest result is *blocked*, not a redirect.

**Evidence of a real run:** the fixture-based set takes ~10–15s (drop/create + `MigrateAsync` + seed), `docker ps` shows a `testcontainers` container during the run, and each assertion-bearing test appears as passed individually. A report in which all fixture tests are `Skipped` is valid only when `BEACON_TEST_SKIP_POSTGRES` was set by choice; `Failed` with the infrastructure diagnostic means Docker or the URL was unreachable — report the run as blocked and capture the startup error.

CI reference (`.github/workflows/ci.yml`): **all** test jobs (Linux, Windows, macOS) supply `BEACON_TEST_DATABASE_URL=postgres://beacon:beacon@localhost:5432/beacon` from a disposable Actions Postgres service, so the integration assertions — including `MigrationApplyTests` against the throwaway service DB — execute on every platform.

### C. Playwright P1.5 E2E suite (`tests/playwright/p15/`)

Prerequisites (all verified):

- Web app running at **http://localhost:5083** in `Development` (§3).
- `beacon-postgres` healthy (the suite seeds and cleans its own rows through `docker exec beacon-postgres psql`).
- `tests/playwright/.dev-admin.json` with the admin `login`/`password` used to revoke memberships via the admin API (`p15/seed.js` reads it).
- Ports **5084** and **5085** free: the suite starts its own WS-drop proxy (`p15/ws-proxy.js`) on `127.0.0.1:5084` (TCP forward to `127.0.0.1:5083`) with a control endpoint on `127.0.0.1:5085` (`POST /dropws`, `GET /count`). No manual proxy step — `p15.spec.js` calls `startProxy()` itself.
- Note: Playwright `context.setOffline(true)` does **not** tear down open WebSockets in Chromium; the proxy's socket destroy is the only working circuit-drop mechanism (documented in `ws-proxy.js`).

Command (working directory `tests/playwright/p15`):

```
npx playwright test --config p15.config.js
```

- Configuration (`p15.config.js`): 1 worker, 0 retries, 8-minute timeout per test, `baseURL http://localhost:5083`, headless.
- Seeding (`p15/seed.js`): per-case org + two projects (A older than B) + users, all rows prefixed `p15-`/`P15-` for exact cleanup; shared user password `P15-e2e-pass-2026` (BCrypt hash checked in). Revocations are performed by the seeded global admin via the API.
- The suite is **timed against the app's 60s circuit revalidation tick** (never shorten the app's interval to make it faster): expect the two tick-driven scenarios to take ~60s each and a full run ~3–5 minutes.
- Success criteria: 4 tests / 5 scenarios pass — S1 in-circuit flip to the remaining project without reload, S2 in-circuit sign-out (full document reload to `/login`), S3a/S3b rebind targets, S4 reconnect-time rebind (<1 tick), S5 cross-user circuit isolation. Verified 2026-10-14: 4/4 passed in 3.2m against the committed spec (log `p15-e2e-utf8.log`).
- If the run is interrupted, cleanup: the suite deletes its own `P15-`/`p15-` rows via `cleanupCase`/`cleanupAll` on normal completion; on interruption the same `p15-`-prefixed rows are the exact cleanup set.
- Other Playwright assets are legacy and not part of this path: `tests/playwright/playwright.config.json` (testDir `./screenshots`), `sprint6-check.js`, `screenshot-matrix.js`.

## 5. What counts as evidence in a final report

For every validation run, record:

1. Exact command, working directory, and exit code.
2. Test counts as reported by the runner (passed/failed/skipped).
3. Log path (this environment uses `C:\Users\vtlyk\AppData\Local\Temp\opencode\...`).
4. Whether the run reached its required dependency (container started, app listening, DB migrated) — a green exit code alone is insufficient when tests can pass vacuously (§4B).
5. Anything explicitly **not** verified, and why (e.g. tool unavailable, fixture unavailable, unverified configuration-derived command).

## 6. Safe cleanup rules

- Stop only processes started by the current task (record the PID or use the port you started; `netstat -ano` maps port → PID on Windows).
- Never reset the shared `beacon` database, recreate `beacon-postgres` or the `pgdata` volume, or run `docker compose down` on the shared stack without explicit authorization.
- Never point `BEACON_TEST_DATABASE_URL` at the shared development or production database.
- Never print `.env` values, `bcn_`/`bcd_`/`bci_`/`bcr_` tokens, `BEACON_WORKER_TOKEN`, or connection strings with passwords.
- Testcontainers databases are disposable and removed with the container; no shared state is touched.

## 7. Troubleshooting (configuration-derived fixes only)

| Symptom | Check / fix |
|---|---|
| `connection refused` on 5432 | `docker ps --filter name=beacon-postgres`; if absent, `docker compose up -d` (do not recreate a running one). |
| Container `Up` but not healthy | `docker inspect --format='{{.State.Health.Status}}' beacon-postgres`; wait for the `pg_isready` healthcheck (5s interval, 10 retries). |
| Web fails with `Set ConnectionStrings:Default ...` | `.env` not found from the CWD — start from the repo root (`dotnet run --project ...` does this), or export the variables in the shell. |
| Port 5083 already in use | Identify the owner (`netstat -ano`); reuse a healthy instance or stop only a process this task started. |
| Playwright: `browserType.launch: executable doesn't exist` | `npx playwright install chromium` in `tests/playwright`. **(unverified command path; environment already has browsers)** |
| Playwright: S4 hangs before reconnect evidence | Verify the proxy is up (suite logs `proxy dropped N circuit socket(s)`); confirm nothing else binds 5084/5085. |
| API.Tests PostgreSQL tests all `Failed` with "PostgreSQL test infrastructure is unavailable" | Docker daemon down or `BEACON_TEST_DATABASE_URL` unreachable. Start Docker or fix the URL; do not redirect to the shared DB. `BEACON_TEST_SKIP_POSTGRES=1` is only for an intentional, reported skip. |
| `dotnet ef` not found | `dotnet tool restore` (local tool `dotnet-ef 9.0.0` declared in `dotnet-tools.json`), then e.g. `dotnet ef migrations has-pending-model-changes --project ProjectBeacon.Infrastructure --startup-project ProjectBeacon.Infrastructure` (CI step). |

## 8. Sources

| Fact | Source |
|---|---|
| Ports 5083/5183/7118, profiles, `beacon_test` override | `ProjectBeacon.Web/Properties/launchSettings.json` |
| Postgres container/image/volume/port, healthcheck | `docker-compose.yml`, `ProjectBeacon.Web/Startup/DockerPostgresHelper.cs` |
| `.env` discovery and precedence | `ProjectBeacon.Infrastructure/Data/EnvFile.cs`, `.env.example` |
| Roles, password resolution, `EnsureAppRoleAsync` | `ProjectBeacon.Infrastructure/Data/PostgresConnection.cs` |
| Migrate-on-start, `BEACON_MIGRATE_ON_START`, `BEACON_MIGRATE_THEN_EXIT` | `ProjectBeacon.Web/Program.cs` |
| Fixture selection, `RequirePostgres`, `BEACON_TEST_SKIP_POSTGRES`, `Available` | `ProjectBeacon.API.Tests/PostgresFixture.cs` |
| `beacon_migrate_test` drop/create | `ProjectBeacon.API.Tests/MigrationApplyTests.cs` |
| P1.5 suite config, seeding, proxy, ports 5084/5085 | `tests/playwright/p15/{p15.config.js, seed.js, ws-proxy.js, p15.spec.js}` |
| CI commands, GPU-lane filter, `BEACON_TEST_DATABASE_URL` usage | `.github/workflows/ci.yml`, `test.runsettings` |
| SDK pin, EF local tool, product version | `global.json`, `dotnet-tools.json`, `Directory.Build.props` |
| Self-host walkthrough, `/bootstrap`, invite-only registration | `README.md` |
