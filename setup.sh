#!/usr/bin/env bash
# Install local Beacon agent settings. Paste a project token when the console asks.
# The token is hidden and is never written into Grok / Cursor / Claude configs.

set -euo pipefail
cd "$(dirname "$0")"

flag_value() {
  local name="$1"
  shift
  while [ "$#" -gt 0 ]; do
    case "$1" in
      "$name")
        printf '%s' "${2-}"
        return 0
        ;;
      "$name"=*)
        printf '%s' "${1#"$name"=}"
        return 0
        ;;
    esac
    shift
  done
  return 1
}

read_secret() {
  local prompt="$1"
  local value=""
  printf '%s: ' "$prompt" >&2
  if [ -t 0 ] && command -v stty >/dev/null 2>&1; then
    stty -echo
    IFS= read -r value || true
    stty echo
    printf '\n' >&2
  else
    IFS= read -r value || true
  fi
  printf '%s' "$value"
}

if ! command -v pnpm >/dev/null 2>&1; then
  echo "pnpm is required. Install Node.js 22+ and pnpm, then re-run setup.sh." >&2
  exit 1
fi

echo "ProjectBeacon setup"
echo "Mint a project token in Agents, then paste it when asked. Input is hidden."
echo "Setup writes BEACON_HOME and local MCP snippets. It does not mint tokens or start a hosted agent."
echo

args=("$@")
token="$(flag_value --token "$@" || true)"
project="$(flag_value --project "$@" || true)"
url="$(flag_value --url "$@" || true)"

if [ -z "$token" ]; then
  token="$(read_secret "Paste project token (hidden, starts with bcn_)")"
  token="${token#"${token%%[![:space:]]*}"}"
  token="${token%"${token##*[![:space:]]}"}"
  if [ -z "$token" ]; then
    echo "A project token is required. Mint one in Agents, then paste it here." >&2
    exit 1
  fi
fi

if [ -z "$project" ]; then
  printf 'Project id: '
  IFS= read -r project || true
  project="${project#"${project%%[![:space:]]*}"}"
  project="${project%"${project##*[![:space:]]}"}"
  if [ -z "$project" ]; then
    echo "A project id is required." >&2
    exit 1
  fi
fi

if [ -z "$url" ]; then
  default_url="${BEACON_URL:-http://127.0.0.1:8080}"
  printf 'Control plane URL [%s]: ' "$default_url"
  IFS= read -r url || true
  url="${url#"${url%%[![:space:]]*}"}"
  url="${url%"${url##*[![:space:]]}"}"
  if [ -z "$url" ]; then
    url="$default_url"
  fi
fi

if ! flag_value --cwd "${args[@]}" >/dev/null; then
  args+=(--cwd "$(pwd)")
fi

export BEACON_SETUP_TOKEN="$token"
export BEACON_SETUP_PROJECT="$project"
export BEACON_SETUP_URL="$url"
exec pnpm --filter @beacon/cli start -- setup "${args[@]}"
