# Control-plane deploy (Kubernetes blue-green)

Ships **Web** (Blazor + `/v1`) as one image. It serves the customer application on the existing public host and the isolated operator Control Plane on `control.projectbeacon.tech`; both ingress hosts target the Web service, while host middleware keeps their route surfaces separate. Postgres is shared and is not blue-green. `beacon client` on developer machines is **not** auto-updated.

## Image

```
docker build -t ghcr.io/<org>/projectbeacon-web:<tag> \
  --build-arg BEACON_VERSION=0.1.0 \
  --build-arg BEACON_GIT_SHA=$(git rev-parse --short HEAD) .
```

`BEACON_VERSION` becomes the assembly `Version`. `BEACON_GIT_SHA` is a runtime env var, not `InformationalVersion`.

Replace `OWNER` in `deploy/k8s/*.yaml` with the GHCR owner and `__PINNED_TAG__` with the immutable version tag (e.g. `v1.2.3`). Never run from a mutable `latest` tag.

Containers run as non-root (UID `1654`, the `app` user of the base image) with a read-only root filesystem and a writable `emptyDir` at `/tmp`. The namespace enforces `restricted` Pod Security Admission.

Container env: `BEACON_MIGRATE_ON_START=false`. Local `dotnet run` still migrates (non-Production).

## Role guard (fail-closed)

Every Production host verifies the **application** role (the `ConnectionStrings__Default` login, normally `beacon_app`) against `pg_roles` at startup, using the actual connected role (`current_user`), before serving or exiting:

- Web pods: start is refused when the role has `SUPERUSER` or `BYPASSRLS`, or when the role cannot be verified (connection/query failure). The pod never becomes ready.
- Migrate Job: exits non-zero in the same cases, so a misconfigured secret stops the deploy before traffic moves.
- The check never reads or logs connection strings or passwords; diagnostics carry the role name and flags only.
- The migration role (`ConnectionStrings__Migration`, superuser) is intentionally **not** checked — it only applies schema changes and bootstraps `beacon_app`.

Development/Testing hosts log a warning and continue; the guard is skipped entirely in the `Testing` environment.

## First apply

1. `kubectl apply -f deploy/k8s/namespace.yaml`
2. Create the secret (see `secret.yaml.example`). Set `BEACON_PUBLIC_URL` to the customer
   application hostname (the ingress customer host), not `control.projectbeacon.tech`.
   Do not commit real secrets.
3. Replace `OWNER` with your GHCR owner and `__PINNED_TAG__` with the immutable version tag in `deploy/k8s/*.yaml`, then:
   ```
   kubectl apply --dry-run=server -f deploy/k8s/web.yaml -f deploy/k8s/ingress.yaml
   kubectl apply -f deploy/k8s/web.yaml -f deploy/k8s/ingress.yaml
   ```
   The `--dry-run=server` pass is required: client-side YAML checks do not validate against the cluster's admission policies (`restricted` PSA) or the API schema.
4. Run a migrate Job once (`BEACON_MIGRATE_ON_START=true`, `BEACON_MIGRATE_THEN_EXIT=true`).
5. Ingress needs nginx (or equivalent) with **cookie affinity** — Blazor Server circuits. Before sending traffic, provision the TLS secret `beacon-web-tls` in namespace `beacon`; its certificate SANs must include both the customer hostname (`beacon.example.com` in the sample manifest) and `control.projectbeacon.tech`. Use the cluster's existing cert-manager/ACME issuer or your certificate-management process; this repository does not assume a cluster issuer.

The ingress manifest includes both the existing customer host and `control.projectbeacon.tech`,
routed to the same Web service. Configure DNS records for both hostnames to the ingress address
(an A/AAAA record or a CNAME where appropriate). The TLS section references `beacon-web-tls`,
which must contain a certificate covering both hostnames. Set `CONTROL_HOST=control.projectbeacon.tech`
and `BEACON_PUBLIC_URL=https://<customer-hostname>` in the `beacon-web` secret/environment.
`BEACON_PUBLIC_URL` is the customer-facing origin used to build invite and password-reset links;
never set it to the Control Plane host. Production invitation creation fails closed if this origin
is missing, not HTTPS, or points to the Control Plane. The application returns `404` for Control Plane paths
on the customer host; the standalone public API host independently rejects operator, bootstrap,
and recovery endpoints.

## Switch

```
sh deploy/k8s/switch.sh green ghcr.io/<org>/projectbeacon-web:v1.2.3
```

Order: migrate Job → scale idle color to 1 → wait Available → patch Service selector → scale old color to 0.

Breaking schema changes need expand/contract across two releases. Additive EF migrations can run in the Job before traffic moves.

## Data-key rotation

Stored secrets (TOTP, OpenCode API keys, SMTP password) are encrypted with `DATA_PROTECTION_KEY`, separate from the JWT signing key. New writes use the v2 format (`v2:` prefix, HKDF-derived AES-GCM key) only when `DATA_PROTECTION_WRITE_V2=true`; otherwise they stay v1, which is read with `DATA_PROTECTION_LEGACY_KEY`. Rotating the key is a multi-step rollout because v2 ciphertexts are unreadable without the key that wrote them:

1. Add `DATA_PROTECTION_KEY=<new key>` and `DATA_PROTECTION_LEGACY_KEY=<old key>` to the secret. Deploy. Writes stay v1; old rows remain readable through the legacy key.
2. Set `DATA_PROTECTION_WRITE_V2="true"` in the secret and deploy. New writes are v2; v1 rows are still read via the legacy key.
3. Stop old instances' traffic (scale the previous color to 0) and run a migrate Job (`BEACON_MIGRATE_ON_START=true`, `BEACON_MIGRATE_THEN_EXIT=true`). It re-encrypts v1 rows to v2 in place, never touching other columns. Its log reports `migrated`, `remaining_v1`, and `failed`. Re-run until `remaining_v1=0` and `failed=0`. Rows that fail keep their original ciphertext — they are counted, not masked or dropped.
4. Remove `DATA_PROTECTION_LEGACY_KEY` from the secret and deploy. If any v1 row is still unreadable, the startup log says the v1 migration is impossible until the old key is provided again.

`failed>0` rows need the old key (step 4 undoes it if you already removed it). The pass is idempotent; a row is rewritten only after it decrypts and re-seals.

## Data Protection key ring

The ASP.NET Data Protection key ring (which protects both the customer `BeaconAuth` and operator
`BeaconControlAuth` cookies)
lives in the shared Postgres database (`DataProtectionKeys` table), not on the
pod file system. Each row is sealed with key material derived from
`DATA_PROTECTION_KEY` (AES-GCM); rows sealed before a key change stay readable
via `DATA_PROTECTION_LEGACY_KEY`. On every start, each web pod seeds the ring
if it is empty (a database advisory lock serializes parallel first starts) and
a fail-closed guard refuses to serve when the ring is unreadable with the
configured key material (wrong key, tampered rows) — and, in Production, when
it is empty.

- Every host that shares one ring database must use the same
  `DATA_PROTECTION_KEY` — here, every web pod (blue and green). A divergent
  key makes the ring unreadable and the pod never becomes ready.
- Rotating `DATA_PROTECTION_KEY` orphans the existing sealed ring: without the
  old key available as `DATA_PROTECTION_LEGACY_KEY` the host fails closed, and
  a start on a fresh (empty) ring issues new keys that invalidate every
  session — a one-time logout of everyone. To rotate without a logout, deploy
  the new key alongside `DATA_PROTECTION_LEGACY_KEY=<old key>` and keep the
  legacy key for as long as the pre-rotation ring keys must stay readable.
- Losing the database (and its backups) means losing the ring: a fresh
  database is seeded with new keys on start, and every session is invalidated
  once.

### Recovery: reset a wedged ring

If a host fails closed because the ring is unreadable with the configured key
material (wrong `DATA_PROTECTION_KEY`, tampered rows) and neither the old key
nor the old rows can be restored, the ring can be reset. The effect is the
same as losing the database: a fresh ring is seeded and every session is
invalidated once; no application data is touched.

1. Scale all web pods to zero so no pod is serving on the old ring:

   ```
   kubectl scale deployment beacon-web-blue beacon-web-green --replicas=0
   ```

2. Truncate the ring table on the shared database (as the superuser, e.g. via
   the migration role):

   ```
   psql "$ConnectionStrings__Migration" -c 'TRUNCATE "DataProtectionKeys"'
   ```

3. Scale the web pods back up. Each pod seeds the fresh ring on start (the
   advisory lock serializes parallel first starts) and all users are logged
   out once.

## Platform email settings

The optional `MAIL__Host`, `MAIL__Port`, `MAIL__User`, `MAIL__Password`, and `MAIL__From`
environment variables remain a bootstrap/fallback path. Once an operator saves SMTP settings in
the Control Plane, the database configuration takes precedence on every Web replica and applies
without a container restart. The SMTP password is encrypted with `DATA_PROTECTION_KEY`; all Web
replicas must share the same data-protection key configuration. A saved disabled configuration
intentionally overrides and disables the environment fallback.

Use the Control Plane's connection check to validate DNS, TCP, STARTTLS, and SMTP authentication
without sending a message. Use the separate test-email action to verify that the relay accepts an
actual message.

## TLS termination and forwarded headers

Ingress terminates TLS, so the web pods receive plain HTTP and `Request.IsHttps` is false. The app
runs `UseForwardedHeaders` (`X-Forwarded-For`, `X-Forwarded-Proto`) before authentication to restore
`IsHttps` from the forwarded proto. In Production the `BeaconAuth` cookie uses
`SecurePolicy = Always` (fail-closed), so it carries `Secure` even before the forwarded proto is
honored; in other environments it is `SameAsRequest`.

The middleware only honors forwarded headers from trusted sources (default: loopback only). The web
pod sees the ingress controller's pod IP as the client, so add the cluster pod CIDR:

```
FORWARDEDHEADERS__KNOWNNETWORKS__0=<pod CIDR, e.g. 10.244.0.0/16>
```

or pin exact ingress pod IPs with `FORWARDEDHEADERS__KNOWNPROXIES__0=<ingress pod IP>`. For multiple
entries append `__1`, `__2`, ... Check the source address the web pods actually see with
`kubectl get pods -n ingress-nginx -o wide`. Set the variables in the `env:` list of
`deploy/k8s/web.yaml` or in the `beacon-web` secret (`envFrom`), for **both** blue and green Web
deployments.

This is required for the Control Plane on Kubernetes, not merely an optimization: its login and
logout endpoints compare the browser's exact `Origin` with the request scheme and host, and reject
the request with `403` when they differ. Since the ingress terminates TLS, the browser sends an
`https://...` Origin while the pod otherwise sees the upstream request as `http://...`. Trusting
the actual ingress source restores `Request.Scheme=https` before the origin check. In Production,
authentication cookies remain `Secure` independently because their cookie policy is
`SecurePolicy.Always`; that flag does not replace correct forwarded-header handling.

Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` — it bypasses the trust lists and would honor
forwarded headers from any client. Without the variables, forwarded headers are ignored: the
application sees the internal HTTP request, and Control Plane form login/logout fail closed with
`403`.

## CI

Tag `v*` runs `.github/workflows/release.yml` with the gate chain **tests → image → scan → deploy**:

1. `tests` — the full authoritative CI Linux lane (mirror of `build-test-linux` in `ci.yml`, including the TRX guards; keep both in sync). A tag push does not trigger `ci.yml`, so this job is the release test gate.
2. `image` — builds once and pushes **immutable** tags only: the version tag (`v1.2.3`) and the full commit SHA. No `latest`. The push's image **digest** is exported as a job output. Protect `v*` tags on GitHub so they cannot be re-pointed (an additional measure — the chain below does not rely on tag immutability).
3. `scan` — **digest binding + Trivy** (pinned by digest) on the exact pushed image:
   - the tag is resolved via `buildx imagetools inspect` and must equal the digest the `image` job exported; a mismatch (tag re-pointed between jobs) fails the job;
   - Trivy generates, before the gate, the CycloneDX **SBOM** (`sbom.cdx.json`) and the **full** HIGH/CRITICAL report (`trivy-report.json`, fixed **and** unfixed findings); both are uploaded as the `scan-sbom-<sha>` artifact even when the gate fails;
   - the **blocking gate** then runs with `--severity HIGH,CRITICAL --ignore-unfixed --exit-code 1`: fixed HIGH/CRITICAL findings fail the job and block the deploy. Unfixed findings are deliberately excluded from the gate (distro base-image CVEs without an upstream fix) but **remain in the uploaded report and must be explicitly reviewed and tracked on every release**.
4. `deploy` — deploys `repo@sha256:<digest>` using the **digest verified by the scan job**, never a tag: even if the version tag is re-pointed afterwards, the rollout pulls the exact scanned artifact. No rebuild occurs between `image`, `scan`, and `deploy`. Always runs (the `production` environment gate still applies). If the `KUBECONFIG` secret is empty, the job prints a skip message and exits 0.

All workflow actions are pinned by commit SHA.

## Client

Workstation clients keep their installed `beacon` binary. Heartbeat `probeJson.clientVersion` shows the assembly informational version. `GET /v1/version` reports `{ version, gitSha }` — version from the assembly, `gitSha` from `BEACON_GIT_SHA`.
