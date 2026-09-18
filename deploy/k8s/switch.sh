#!/bin/sh
set -eu
# Usage: switch.sh <blue|green> <image> [namespace]
COLOR=${1:?color blue or green}
IMAGE=${2:?image tag, e.g. ghcr.io/org/projectbeacon-web:v1.2.3}
NS=${3:-beacon}

if [ "$COLOR" != "blue" ] && [ "$COLOR" != "green" ]; then
  echo "color must be blue or green" >&2
  exit 2
fi

if [ "$COLOR" = "blue" ]; then
  IDLE=green
else
  IDLE=blue
fi

echo "migrate $IMAGE"
kubectl -n "$NS" delete job beacon-migrate --ignore-not-found
sed "s|image: ghcr.io/OWNER/projectbeacon-web:latest|image: ${IMAGE}|" "$(dirname "$0")/migrate-job.yaml" \
  | kubectl apply -f -
kubectl -n "$NS" wait --for=condition=complete job/beacon-migrate --timeout=180s

echo "rollout $COLOR"
kubectl -n "$NS" set image "deployment/beacon-web-${COLOR}" "web=${IMAGE}"
kubectl -n "$NS" scale "deployment/beacon-web-${COLOR}" --replicas=1
kubectl -n "$NS" rollout status "deployment/beacon-web-${COLOR}" --timeout=180s

echo "switch service -> $COLOR"
kubectl -n "$NS" patch service beacon-web -p "{\"spec\":{\"selector\":{\"app\":\"beacon-web\",\"color\":\"${COLOR}\"}}}"

echo "scale down $IDLE"
kubectl -n "$NS" scale "deployment/beacon-web-${IDLE}" --replicas=0
echo "active color: $COLOR"
