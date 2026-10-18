#!/usr/bin/env bash
# Logical backup of the beacon database: pg_dump (plain SQL) -> gzip -> BACKUP_DIR,
# with 14-day local retention and an optional off-host copy via rclone.
#
# Usage:
#   ./backup.sh
#
# Runs fine from cron, e.g. daily:
#   15 3 * * * /path/to/deploy/compose/backup.sh >> /var/log/beacon-backup.log 2>&1
set -euo pipefail
cd "$(dirname "$0")"

set -a
. ./.env
set +a

BACKUP_DIR="${BACKUP_DIR:-/var/backups/beacon}"
TS="$(date +%Y%m%d-%H%M%S)"
DEST="$BACKUP_DIR/beacon-$TS.sql.gz"

mkdir -p "$BACKUP_DIR"

docker compose exec -T postgres pg_dump -U beacon beacon | gzip > "$DEST"

# Verify the archive before touching retention or off-host copies.
gzip -t "$DEST"
SIZE="$(stat -c%s "$DEST")"
echo "backup written: $DEST ($SIZE bytes)"

# Retention: keep the 14 newest local backups.
ls -1t "$BACKUP_DIR"/beacon-*.sql.gz 2>/dev/null | tail -n +15 | while read -r old; do
  rm -f "$old"
done

# Optional off-host copy (requires rclone; the remote must already exist).
if [ -n "${RCLONE_REMOTE:-}" ]; then
  rclone copyto "$DEST" "$RCLONE_REMOTE/beacon-$TS.sql.gz"
  echo "off-host copy: $RCLONE_REMOTE/beacon-$TS.sql.gz"
else
  echo "RCLONE_REMOTE not set: local copy only"
fi
