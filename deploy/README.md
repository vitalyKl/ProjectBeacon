# Control-plane deploy (Kubernetes blue-green)

Ships **Web** (Blazor + `/v1`) as one image. Postgres is shared and is not blue-green. `beacon client` on developer machines is **not** auto-updated.

## Image

```
docker build -t ghcr.io/<org>/projectbeacon-web:<tag> --build-arg BEACON_GIT_SHA=$(git rev-parse --short HEAD) .
```

Replace `OWNER` in `deploy/k8s/*.yaml` with the GHCR owner.

Container env: `BEACON_MIGRATE_ON_START=false`. Local `dotnet run` still migrates (non-Production).

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

## CI

Tag `v*` runs `.github/workflows/release.yml`: build/push GHCR. Deploy job runs only if `KUBECONFIG` is set on the `production` environment.

## Client

Workstation clients keep their installed `beacon` binary. Heartbeat `probeJson.clientVersion` shows the assembly version. `GET /v1/version` reports control-plane version and `BEACON_GIT_SHA`.
