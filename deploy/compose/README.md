# Closed-alpha deploy: single Ubuntu VPS (Docker Compose)

Brings up **Web + Worker + PostgreSQL + Caddy** on one VPS. No Kubernetes, no
auto-deploy: every deployment is a manual run of `upgrade.sh` with an explicit
image reference. The local development stack (repo-root `docker-compose.yml`)
and the Kubernetes manifests (`deploy/k8s/`) are untouched and stay independent.

This guide is the single place with exact commands. It was written against the
current checkout; anything not executed is marked **(not verified)**.

## Topology

```
internet ── 22 / 80 / 443 ──► Caddy (TLS, Let's Encrypt)
                                   │  (only 80/443 published)
              beacon-alpha-internal (172.29.0.0/16)
                                   ├─ web      :8080   (read-only, caps dropped)
                                   ├─ worker          (read-only, caps dropped)
                                   └─ postgres :5432   (internal only, no published ports)
migrate — one-shot container of the same image (profile "migrate")
```

Note: the compose network is declared as `beacon-alpha-internal`, but Docker
names it `beacon-alpha_beacon-alpha-internal` (the project name is prefixed
even with an explicit `name:`). Use that name in `docker network inspect`
and `docker inspect` `.Networks` keys.

- **postgres** is reachable only from the internal network. Nothing publishes
  5432 to the host or the internet.
- **Caddy** terminates TLS (Auto-HTTPS / ACME) and proxies to `web:8080`,
  forwarding `X-Forwarded-Proto`; Blazor Server WebSockets pass through
  natively (no stream timeout).
- **web** runs with `BEACON_MIGRATE_ON_START=false` and the fail-closed role
  guard (Production refuses to start when the app role bypasses RLS or cannot
  be verified).
- **worker** expires sessions/API tokens/reset tokens/invites every 30
  minutes and recovers stale workstation commands. It shares the image with
  web (`/app/worker/ProjectBeacon.Worker.dll`).

## Prerequisites on the VPS

- Ubuntu 22.04/24.04, Docker Engine + Docker Compose v2.
- `curl`, `rclone` (only if off-host backups are used).
- Firewall (both the VPS provider panel and `ufw`): allow **22** (ideally from
  your IP only), **80**, **443**; deny everything else inbound. 5432 must not
  be reachable from outside the docker network.

## Initial install

1. Copy `deploy/compose/` to the VPS (e.g. `/opt/beacon/deploy/compose`). No
   build happens on the VPS — images come from GHCR.

2. Create the env file and generate secrets (never print or commit them):

   ```bash
   cd /opt/beacon/deploy/compose
   cp .env.example .env
   chmod 600 .env
   # generate values, e.g.:  openssl rand -base64 48
   #   POSTGRES_PASSWORD, JWT__Secret, DATA_PROTECTION_KEY,
   #   BOOTSTRAP_ADMIN_TOKEN, BEACON_WORKER_TOKEN
   ```

   Keep `DATA_PROTECTION_WRITE_V2=true` (fresh install). Set
   `BEACON_DOMAIN` (your A record) and `ACME_EMAIL`. For invite emails set
   `MAIL__Host` / `MAIL__From`.

   Keep `.env` on Unix line endings (LF). A CRLF file puts a hidden carriage
   return into `BEACON_WEB_IMAGE`, and `docker compose run` then rejects it
   as an invalid image reference.

3. DNS: point an **A record** `beacon.example.com` → VPS IP and wait for
   propagation (`dig +short beacon.example.com`). Ports 80/443 must be open
   before Caddy can issue the certificate.

4. Get an image reference. Images are published only by the release workflow
   on `v*` tags (`.github/workflows/release.yml`: tests → image → scan →
   deploy). Take the **digest** from the release's `scan` job output, or:

   ```bash
   docker buildx imagetools inspect ghcr.io/vitalykl/projectbeacon-web:vX.Y.Z
   # use the "Digest:" line as BEACON_WEB_IMAGE=repo@sha256:<digest>
   ```

   Registry login (a PAT with `read:packages`): `docker login ghcr.io`.

5. Start Postgres and run the one-shot migration:

   ```bash
   docker compose up -d postgres
   docker compose --profile migrate run --rm migrate
   ```

   A non-zero exit means fail-closed (role guard or migration error) — read
   the log and fix; do not start web over a failed migrate. Expected log
   lines: migrations applied, `Data key migration: migrated=0 remainingV1=0
   failed=0` on a fresh database.

6. Start the services:

   ```bash
   docker compose up -d web worker caddy
   ```

7. Verify:

   ```bash
   curl -fsS https://beacon.example.com/health          # {"status":"healthy"}
   curl -fsS https://beacon.example.com/v1/version      # {"version":"X.Y.Z","gitSha":"..."}
   ```

   Then in a browser: `https://beacon.example.com/bootstrap` (enter
   `BOOTSTRAP_ADMIN_TOKEN`; the generated admin password is shown **once**),
   log in at `/login`, create the org/project. With
   `AUTH_LOCAL_INVITE_ONLY=true` (set in `.env.example`) registration requires
    an invite.

8. Record the deployed digest (first successful state) so upgrades can roll
   back:

   ```bash
   mkdir -p .state && echo "$BEACON_WEB_IMAGE" > .state/last-known-good.txt
   chmod 600 .state/last-known-good.txt
   ```

   (Subsequent `upgrade.sh` runs do this automatically.)

9. Backups: add the daily cron entry from `backup.sh` header.

## Updating (deploy a release tag or digest)

```bash
cd /opt/beacon/deploy/compose
./upgrade.sh ghcr.io/vitalykl/projectbeacon-web:v1.2.3
# or pin exactly what the release scan verified:
./upgrade.sh ghcr.io/vitalykl/projectbeacon-web@sha256:<digest>
```

The script: resolves the reference to a digest (a reference that already
exists as a local image is deployed as-is, no registry pull) → updates
`.env` → `docker compose pull` (registry refs only) → runs the **migrate
one-shot** (gate, `--no-deps`: a missing database fails it; bounded to
300s, so a corrupt image whose migrate never exits also fails and rolls
back) → swaps web+worker → waits for `/health` 200 (120s) → prints
`/v1/version` → records the deployed reference in `.state/last-known-good.txt`. When the script runs
from a separate admin container instead of the VPS itself, set
`BEACON_HEALTH_HOST` (e.g. `host.docker.internal`).

On **any** failure it restores the previous digest from the state file,
restarts web+worker, and waits for health again. Pass `--no-rollback` to keep
the failed state in place for forensics. There is no automatic deployment on
push: `release.yml` does not reach this VPS (no SSH/credentials); this script
is the only deploy path and requires a person on the server.

### Rollback without upgrade.sh

```bash
sed -i "s|^BEACON_WEB_IMAGE=.*|BEACON_WEB_IMAGE=<old-digest>|" .env
docker compose up -d web worker
curl -fsS https://beacon.example.com/health
```

### Schema changes

Migrations are applied **forward-only** by the one-shot migrate (same contract
as the k8s migrate Job). Breaking schema changes must follow expand/contract
across two releases (see `deploy/README.md`). For an emergency schema
rollback, generate the down SQL from the source checkout and apply it as the
superuser:

```bash
dotnet ef migrations script --idempotent --from <older-migration> --to <newer-migration> \
  --project ProjectBeacon.Infrastructure --startup-project ProjectBeacon.Infrastructure
```

(never verified against the VPS database; test the script on a copy first).

## Backup and restore

- `./backup.sh` — `pg_dump` (plain) → gzip → `$BACKUP_DIR` (default
  `/var/backups/beacon`), verifies the archive, keeps 14 newest local copies,
  and optionally copies to an off-host rclone remote (`RCLONE_REMOTE=
  s3:bucket/path` in `.env`; rclone config lives outside this file).
- Restore (downtime required):

  ```bash
  ./restore.sh /var/backups/beacon/beacon-20261017-031500.sql.gz
  # or: ./restore.sh   (newest backup)
  ```

  The script stops web+worker, drops and recreates the `beacon` database,
  streams the dump in as `beacon`, runs the migrate one-shot to align the
  schema with the current image (the dump contains `__EFMigrationsHistory`,
  so only pending migrations apply), then starts the stack and gates on
  `/health`. Verify a known row afterwards (e.g. the admin user in `/Users`
  admin screen).

## Diagnostics

| Symptom | Check |
|---|---|
| `/health` 503 | DB unreachable from web: `docker compose logs postgres`, `docker compose exec postgres pg_isready -U beacon -d beacon` |
| web container crash-loops with role-guard message | `ConnectionStrings__Default` points at a privileged role or the DB is down — fix the connection, never disable the guard |
| Caddy 502/503 | Upstream health (`health_uri /health`) failed: Caddy answers 503 "no upstreams available" once it marks web down. Check web container state and `docker compose logs web` |
| Log: "Failed to determine the https port for redirect" | Expected: web binds `http://8080` only, so the app-level redirect cannot build a URL; Caddy does the HTTP→HTTPS 308 instead. No action needed |
| No certificate / site down on 443 | DNS not propagated, or 80/443 not open (ACME HTTP-01): `docker compose logs caddy` |
| Secure cookies / HTTPS redirect missing | `FORWARDEDHEADERS__KNOWNNETWORKS__0` must equal the docker subnet (172.29.0.0/16); check `docker inspect --format '{{range $n, $i := .Networks}}{{$n}}: {{$i.IPAddress}}{{end}}' beacon-alpha-caddy` (prints `beacon-alpha_beacon-alpha-internal: 172.29.0.x`) |
| Rate limits hit by everyone at once | Same as above: X-Forwarded-For not honored, limiter keys all clients on the proxy IP |
| Invite emails not sent | `MAIL__Host`/`MAIL__From` unset or SMTP auth failing: `docker compose logs web` |
| Worker not cleaning expired records | `docker compose logs worker` — role guard or connection failure; the first tick is up to 30 min after start |

`docker compose ps` — container states; `docker compose logs -f web worker caddy`
— logs. `/v1/version` reports the running image's version and `gitSha`.

## Security notes

- Secrets live only in `deploy/compose/.env` (chmod 600, git-ignored) and in
  Postgres. Nothing secret is in this directory's committed files.
- The app connects as `beacon_app` (`NOSUPERUSER NOBYPASSRLS`) with FORCE RLS
  policies; migrations run as `beacon` (superuser) only during the one-shot
  migrate. Production hosts refuse to start otherwise (fail-closed).
- Containers run read-only, `cap_drop: ALL`, `no-new-privileges`, with `/tmp`
  as the only writable mount. web/worker/migrate run as the non-root `app`
  image user; Caddy runs as root with only `cap_add: NET_BIND_SERVICE`
  (required to bind 80/443 — everything else stays dropped).
- The bootstrap endpoint works only until the first admin exists (then 409);
  the token stays in `.env` but grants nothing afterwards. Rotate
  `BOOTSTRAP_ADMIN_TOKEN` anyway and keep `.env` out of backups unless
  encrypted.
- Do not point `BEACON_TEST_DATABASE_URL` or any tooling at this database.

## Local verification (performed, on Docker Desktop)

The full stack was exercised end-to-end locally with a test Caddy override
(internal TLS for `beacon.test` instead of public ACME):

- fresh database: migrate one-shot OK (28 migrations, roles `beacon`
  superuser / `beacon_app` NOSUPERUSER NOBYPASSRLS, FORCE RLS on); web+worker
  + Caddy up; `https /health` 200; `/v1/version` correct.
- auth: bootstrap (200 once, 409 afterwards) → cookie-login 302 + Secure
  `BeaconAuth` cookie → `/v1/auth/me` and `/dashboard` 200; wrong password
  redirects without a cookie; rate limit returns 429 after 5 auth requests
  per 60s per client.
- forwarded headers: from the internal subnet (172.29.0.0/16) X-Forwarded-For
  is honored (rate-limit buckets partition by it); client-side XFF spoofing
  through Caddy does NOT partition; Caddy itself issues the HTTP→HTTPS 308.
- role guard: web pointed at the superuser role exits non-zero (fail-closed)
  with a precise diagnostic.
- postgres down: Caddy answers 503 ("no upstreams available"), web stays up
  without crash-looping; healthy again after postgres restart.
- `backup.sh`: archive written and gzip-verified; `restore.sh`: DB
  drop/recreate + dump + migrate + health gate; admin login still works
  after restore.
- `upgrade.sh`: success path (v1→v2, state file recorded, version printed);
  DB-down failure (migrate gate fails, rollback restores health); broken
  image (migrate OK, web dead → 120s health-gate timeout → auto-rollback);
  hung migrate (300s bounded gate → rollback, orphaned one-shot container
  removed).

## What is NOT verified in this environment

- No real Contabo VPS run: DNS, public Let's Encrypt issuance (the local
  test used `tls internal`), provider firewall, and rclone off-host copy
  were not executed.
- UI flows were verified with HTTP requests (curl), not in a browser.
- The 30-minute worker cleanup tick was not observed live (service start and
  role-guard pass were verified; the first tick is deferred).
- Schema rollback via `migrations script` is documented only; test it on a
  database copy before relying on it.
