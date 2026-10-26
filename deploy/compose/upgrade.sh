#!/usr/bin/env bash
# Deploys a given image reference (digest or tag) to the VPS stack and rolls
# back automatically on failure. Never deploys on a schedule or by push —
# this script is invoked manually or by the protected release workflow.
#
# Usage:
#   ./upgrade.sh <image-reference>            # auto-rollback enabled
#   ./upgrade.sh <image-reference> --expected-git-sha <40-char-sha>
#   ./upgrade.sh <image-reference> --no-rollback
#
# <image-reference> is either repo@sha256:<digest> (preferred) or repo:<tag>.
# A registry reference is resolved to a digest first; the deploy always uses
# the digest. A reference that already exists as a local image (e.g. a
# locally built test image, no registry involved) is deployed as-is without
# a registry pull.
set -euo pipefail
cd "$(dirname "$0")"

REF=""
NO_ROLLBACK=0
EXPECTED_GIT_SHA=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --no-rollback)
      NO_ROLLBACK=1
      shift
      ;;
    --expected-git-sha)
      if [ "$#" -lt 2 ]; then
        echo "ERROR: --expected-git-sha requires a value." >&2
        exit 2
      fi
      EXPECTED_GIT_SHA="$2"
      shift 2
      ;;
    -*)
      echo "ERROR: unknown option: $1" >&2
      exit 2
      ;;
    *)
      if [ -n "$REF" ]; then
        echo "ERROR: only one image reference may be supplied." >&2
        exit 2
      fi
      REF="$1"
      shift
      ;;
  esac
done

if [ -z "$REF" ]; then
  echo "usage: $0 <image-reference> [--expected-git-sha <sha>] [--no-rollback]" >&2
  exit 2
fi
if [ -n "$EXPECTED_GIT_SHA" ] && ! [[ "$EXPECTED_GIT_SHA" =~ ^[a-f0-9]{40}$ ]]; then
  echo "ERROR: expected Git SHA must be 40 lowercase hexadecimal characters." >&2
  exit 2
fi

STATE_DIR=".state"
STATE_FILE="$STATE_DIR/last-known-good.txt"
OLD_REF=""
if [ -f "$STATE_FILE" ]; then
  OLD_REF="$(cat "$STATE_FILE")"
fi

DOMAIN="$(sed -n 's/^BEACON_DOMAIN=//p' .env)"
if [ -z "$DOMAIN" ]; then
  echo "ERROR: BEACON_DOMAIN is not set in .env." >&2
  exit 2
fi
# Base host for the health gate. 127.0.0.1 on the VPS; override when the
# script runs from a separate admin container (e.g. host.docker.internal).
HEALTH_HOST="${BEACON_HEALTH_HOST:-127.0.0.1}"
# curl --resolve accepts IP literals only; allow a resolvable hostname override.
HEALTH_IP="$HEALTH_HOST"
if ! printf '%s' "$HEALTH_HOST" | grep -Eq '^([0-9]{1,3}\.){3}[0-9]{1,3}$|^[0-9a-fA-F:]+$'; then
  HEALTH_IP="$(getent hosts "$HEALTH_HOST" | awk '{print $1; exit}')"
  if [ -z "$HEALTH_IP" ]; then
    echo "ERROR: cannot resolve health host '$HEALTH_HOST'." >&2
    exit 2
  fi
fi

# --- 1. Make the image available and resolve its digest ---------------------
DEPLOY_REF=""
if docker image inspect "$REF" >/dev/null 2>&1; then
  echo "image $REF is already local; deploying it as-is (no registry pull)"
  DEPLOY_REF="$REF"
else
  DIGEST="$(docker buildx imagetools inspect "$REF" 2>/dev/null | sed -n 's/^Digest:[[:space:]]*//p' | head -n 1 || true)"
  if [ -z "$DIGEST" ]; then
    echo "ERROR: could not resolve $REF to a digest (registry lookup failed and no local image exists)." >&2
    exit 1
  fi
  echo "deploying $REF -> $DIGEST"
  DEPLOY_REF="$DIGEST"
fi
if [ -n "$OLD_REF" ]; then
  echo "last known good: $OLD_REF"
else
  echo "no previous state recorded; rollback will not be possible"
fi

wait_healthy() {
  local deadline=$((SECONDS + 120))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if curl -fsSk --resolve "$DOMAIN:443:$HEALTH_IP" "https://$DOMAIN/health" >/dev/null 2>&1; then
      return 0
    fi
    sleep 5
  done
  echo "ERROR: /health did not return 200 within 120s." >&2
  return 1
}

FAILED=0
rollback() {
  if [ "$FAILED" = "1" ]; then
    return
  fi
  FAILED=1
  echo "Upgrade failed; stopping the new rollout." >&2
  # A timed-out or interrupted `compose run` can leave an orphaned one-shot
  # migrate container; remove it before the rollback health gate.
  docker rm -f "$(docker ps -aq --filter 'label=com.docker.compose.project=beacon-alpha' --filter name=migrate-run)" 2>/dev/null || true
  if [ "$NO_ROLLBACK" = "1" ]; then
    echo "--no-rollback set: leaving the stack as is. Investigate with: docker compose ps / logs." >&2
    exit 1
  fi
  if [ -z "$OLD_REF" ]; then
    echo "ERROR: no last-known-good reference recorded; cannot roll back automatically." >&2
    exit 1
  fi
  echo "Rolling back to $OLD_REF ..." >&2
  sed -i "s|^BEACON_WEB_IMAGE=.*|BEACON_WEB_IMAGE=$OLD_REF|" .env
  docker compose up -d web worker || exit 1
  wait_healthy || exit 1
  echo "Rollback complete: stack is back on $OLD_REF." >&2
  exit 1
}

trap rollback ERR

# --- 2. Point the stack at the new reference and pull (registry refs only) --
sed -i "s|^BEACON_WEB_IMAGE=.*|BEACON_WEB_IMAGE=$DEPLOY_REF|" .env
if [ "$DEPLOY_REF" != "$REF" ]; then
  docker compose pull
fi

# --- 3. Migrate first (fail-closed gate) --------------------------------------
# --no-deps: postgres availability is owned by the operator; a missing
# database must fail this gate, not be silently started by a dependency.
# Bounded: a corrupt image whose migrate entrypoint never exits must not
# stall the upgrade forever (timeout -> rollback).
timeout 300 docker compose --profile migrate run --rm --no-deps migrate
echo "Migration pass OK."

# --- 4. Swap web+worker to the new image --------------------------------------
docker compose up -d web worker

# --- 5. Health gate -----------------------------------------------------------
wait_healthy
echo "Health gate passed."

VERSION_JSON="$(curl -fsSk --resolve "$DOMAIN:443:$HEALTH_IP" "https://$DOMAIN/v1/version")"
echo "Running: $VERSION_JSON"
if [ -n "$EXPECTED_GIT_SHA" ]; then
  if ! printf '%s' "$VERSION_JSON" | grep -Eq '"gitSha"[[:space:]]*:[[:space:]]*"'"$EXPECTED_GIT_SHA"'"'; then
    echo "ERROR: deployed application Git SHA does not match expected release $EXPECTED_GIT_SHA." >&2
    false
  fi
fi

# --- 6. Record success --------------------------------------------------------
mkdir -p "$STATE_DIR"
echo "$DEPLOY_REF" > "$STATE_FILE"
trap - ERR
echo "Upgrade complete. State updated to $DEPLOY_REF."
