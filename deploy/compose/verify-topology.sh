#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$COMPOSE_DIR/../.." && pwd)"
ENV_FILE="$COMPOSE_DIR/.env"
RENDERED_CONFIG="$(mktemp)"
cleanup() {
  rm -f "$ENV_FILE" "$RENDERED_CONFIG"
}
trap cleanup EXIT

if [[ -e "$ENV_FILE" ]]; then
  echo "Refusing to overwrite an existing $ENV_FILE." >&2
  exit 1
fi

cp "$COMPOSE_DIR/.env.example" "$ENV_FILE"
# Interpolation-only placeholders; never printed or used to start containers.
export POSTGRES_PASSWORD="compose-ci-placeholder"
export OPS_AGENT_SHARED_SECRET="$(printf 'a%.0s' {1..64})"

docker compose -f "$COMPOSE_DIR/docker-compose.yml" config --format json > "$RENDERED_CONFIG"

python3 - "$RENDERED_CONFIG" "$ROOT_DIR/Dockerfile" <<'PY'
import json
import sys
from pathlib import Path

config_path, dockerfile_path = sys.argv[1:]
config = json.loads(Path(config_path).read_text(encoding="utf-8"))
services = config.get("services", {})
networks = config.get("networks", {})

required = {"web", "worker", "ops-agent", "docker-log-proxy", "postgres", "caddy"}
missing = sorted(required - services.keys())
if missing:
    raise SystemExit(f"::error::Compose is missing required services: {', '.join(missing)}")

internal = "beacon-alpha-internal"
ops_network = "beacon-alpha-ops-internal"
if ops_network not in networks or networks[ops_network].get("internal") is not True:
    raise SystemExit("::error::The OpsAgent/DockerLogProxy bridge must be marked internal.")

def attached(service_name: str) -> set[str]:
    return set(services[service_name].get("networks", {}))

if ops_network not in attached("ops-agent") or internal not in attached("ops-agent"):
    raise SystemExit("::error::OpsAgent must bridge the application network and the isolated ops network.")
if attached("docker-log-proxy") != {ops_network}:
    raise SystemExit("::error::DockerLogProxy must be attached only to the isolated ops network.")
for name in ("web", "worker", "postgres", "caddy", "migrate"):
    if name in services and ops_network in attached(name):
        raise SystemExit(f"::error::{name} must not be attached to the Docker socket network.")

proxy = services["docker-log-proxy"]
if proxy.get("ports"):
    raise SystemExit("::error::DockerLogProxy must not publish a host port.")
if proxy.get("read_only") is not True or proxy.get("user") != "0:0":
    raise SystemExit("::error::DockerLogProxy must use a read-only root filesystem and the socket-owning UID.")

socket_mounts = []
for service_name, service in services.items():
    for volume in service.get("volumes", []):
        if volume.get("target") == "/var/run/docker.sock":
            socket_mounts.append((service_name, volume))
if len(socket_mounts) != 1 or socket_mounts[0][0] != "docker-log-proxy":
    raise SystemExit("::error::Only DockerLogProxy may mount /var/run/docker.sock.")
if socket_mounts[0][1].get("read_only") is not True:
    raise SystemExit("::error::The Docker socket mount must be read-only.")
if services["ops-agent"].get("volumes"):
    raise SystemExit("::error::OpsAgent must not receive a host filesystem mount.")

proxy_environment = proxy.get("environment", {})
agent_environment = services["ops-agent"].get("environment", {})
if proxy_environment.get("ASPNETCORE_URLS") != "http://+:8081":
    raise SystemExit("::error::DockerLogProxy must listen on its private port 8081.")
if agent_environment.get("OPS_AGENT_DOCKER_PROXY_URL") != "http://docker-log-proxy:8081":
    raise SystemExit("::error::OpsAgent must target DockerLogProxy on the isolated network.")

dockerfile = Path(dockerfile_path).read_text(encoding="utf-8")
if "dotnet publish ProjectBeacon.DockerLogProxy/ProjectBeacon.DockerLogProxy.csproj" not in dockerfile:
    raise SystemExit("::error::The production image does not publish the DockerLogProxy executable.")

print("OK: Web → OpsAgent → DockerLogProxy topology is isolated; only DockerLogProxy receives the read-only Docker socket.")
PY
