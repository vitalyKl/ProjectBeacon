#!/usr/bin/env bash
# Restores the beacon database from a backup produced by backup.sh.
#
# Usage:
#   ./restore.sh <backup-file>     # explicit file
#   ./restore.sh                   # newest backup in BACKUP_DIR
#
# Procedure: stop web/worker -> drop and recreate the beacon database as the
# superuser -> stream the dump in -> run the migrate one-shot (aligns the
# schema to the current image; the dump contains __EFMigrationsHistory) ->
# start web/worker -> health gate. The database is unavailable for the whole
# run: coordinate the downtime window.
set -euo pipefail
cd "$(dirname "$0")"

set -a
. ./.env
set +a

BACKUP_DIR="${BACKUP_DIR:-/var/backups/beacon}"
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

FILE="${1:-}"
if [ -z "$FILE" ]; then
  FILE="$(ls -1t "$BACKUP_DIR"/beacon-*.sql.gz 2>/dev/null | head -n 1 || true)"
fi
if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
  echo "ERROR: no backup file found (pass one explicitly). Searched: $BACKUP_DIR" >&2
  exit 1
fi
gzip -t "$FILE"
echo "restoring from $FILE"

echo "stopping web and worker"
docker compose stop web worker

echo "dropping and recreating the beacon database"
docker compose exec -T postgres psql -U beacon -d postgres \
  -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'beacon' AND pid <> pg_backend_pid();" \
  -c "DROP DATABASE beacon;" \
  -c "CREATE DATABASE beacon OWNER beacon;"

echo "streaming the dump"
gunzip -c "$FILE" | docker compose exec -i postgres psql -U beacon -d beacon

echo "aligning schema to the current image (migrate one-shot)"
# Bounded: a corrupt image whose migrate entrypoint never exits must not
# stall the restore forever.
timeout 300 docker compose --profile migrate run --rm migrate

echo "starting web and worker"
docker compose up -d web worker

DEADLINE=$((SECONDS + 120))
while [ "$SECONDS" -lt "$DEADLINE" ]; do
  if curl -fsSk --resolve "$DOMAIN:443:$HEALTH_IP" "https://$DOMAIN/health" >/dev/null 2>&1; then
    echo "restore complete, /health is 200."
    exit 0
  fi
  sleep 5
done
echo "ERROR: /health did not return 200 within 120s after restore." >&2
exit 1
