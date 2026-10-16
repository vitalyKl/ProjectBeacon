# Control-plane deploy (Kubernetes blue-green)

Ships **Web** (Blazor + `/v1`) as one image. Postgres is shared and is not blue-green. `beacon client` on developer machines is **not** auto-updated.

## Image

```
docker build -t ghcr.io/<org>/projectbeacon-web:<tag> \
  --build-arg BEACON_VERSION=0.1.0 \
  --build-arg BEACON_GIT_SHA=$(git rev-parse --short HEAD) .
```

`BEACON_VERSION` becomes the assembly `Version`. `BEACON_GIT_SHA` is a runtime env var, not `InformationalVersion`.

Replace `OWNER` in `deploy/k8s/*.yaml` with the GHCR owner.

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
2. Create the secret (see `secret.yaml.example`). Do not commit real secrets.
3. Point Deployments at your image, then:
   ```
   kubectl apply -f deploy/k8s/web.yaml -f deploy/k8s/ingress.yaml
   ```
4. Run a migrate Job once (`BEACON_MIGRATE_ON_START=true`, `BEACON_MIGRATE_THEN_EXIT=true`).
5. Ingress needs nginx (or equivalent) with **cookie affinity** — Blazor Server circuits.

## Switch

```
sh deploy/k8s/switch.sh green ghcr.io/<org>/projectbeacon-web:v1.2.3
```

Order: migrate Job → scale idle color to 1 → wait Available → patch Service selector → scale old color to 0.

Breaking schema changes need expand/contract across two releases. Additive EF migrations can run in the Job before traffic moves.

## TLS termination and forwarded headers

Ingress terminates TLS, so the web pods receive plain HTTP and `Request.IsHttps` is false. The app
runs `UseForwardedHeaders` (`X-Forwarded-For`, `X-Forwarded-Proto`) before authentication to restore
`IsHttps` from the forwarded proto; the `BeaconAuth` cookie (`SecurePolicy = SameAsRequest`) is then
issued with `Secure` for HTTPS clients.

The middleware only honors forwarded headers from trusted sources (default: loopback only). The web
pod sees the ingress controller's pod IP as the client, so add the cluster pod CIDR:

```
FORWARDEDHEADERS__KNOWNNETWORKS__0=<pod CIDR, e.g. 10.244.0.0/16>
```

or pin exact ingress pod IPs with `FORWARDEDHEADERS__KNOWNPROXIES__0=<ingress pod IP>`. For multiple
entries append `__1`, `__2`, ... Check the source address the web pods actually see with
`kubectl get pods -n ingress-nginx -o wide`. Set the variables in the `env:` list of
`deploy/k8s/web.yaml` or in the `beacon-web` secret (`envFrom`).

Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` — it bypasses the trust lists and would honor
forwarded headers from any client. Without the variables, forwarded headers are ignored and the app
behaves as before (plain HTTP, no `Secure` flag).

## CI

Tag `v*` runs `.github/workflows/release.yml`: build/push GHCR with `BEACON_VERSION` from the tag (`v1.2.3` → `1.2.3`) and `BEACON_GIT_SHA`. The deploy job always runs. If the `KUBECONFIG` secret on the `production` environment is empty, the job prints a skip message and exits 0.

## Client

Workstation clients keep their installed `beacon` binary. Heartbeat `probeJson.clientVersion` shows the assembly informational version. `GET /v1/version` reports `{ version, gitSha }` — version from the assembly, `gitSha` from `BEACON_GIT_SHA`.
