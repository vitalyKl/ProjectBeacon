# ProjectBeacon

Project operating system for mixed human + AI-agent development.

TypeScript monorepo (pnpm workspaces + Turborepo). The web app (`apps/web`) is a Next.js App Router app with auth pages, a context editor, and a same-origin `/v1` rewrite. See [docs/design.md](docs/design.md). The living brief is Context; [AGENTS.md](AGENTS.md) is an export for hosts that only read the repo.

## Prerequisites

- Node.js 22+ or 24 LTS
- [pnpm](https://pnpm.io) 10

## Setup

```bash
pnpm install
pnpm typecheck
```

Workspace scripts: `pnpm build`, `pnpm lint`, `pnpm test`, `pnpm dev`.

Self-host Compose is Postgres 16 + API + web (worker/mcp are not in this compose yet). Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`. `docker compose up` publishes web `:3000` (same-origin `/v1` rewrite) and API `:8080` so host `/health` still works. Later PRs may unpublish the API port.
Self-host Compose is Postgres 16 + API + worker (web/mcp are not in this compose yet). Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` and `BEACON_WORKER_TOKEN`. `docker compose up` publishes API `:8080` so host `/health` and `/ready` work. The worker has no published ports.
Self-host Compose is Postgres 16 + API, MCP Streamable HTTP, and web (`/mcp` reverse-proxy). Worker is not in this compose yet. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`. `docker compose up` publishes API `:8080` and web `:3000`. HTTP MCP is a control plane for context and tasks; code tools need a local sidecar, a self-host bind-mount, or an enabled hosted clone.
Self-host Compose is Postgres 16 + API, MCP Streamable HTTP, and web (`/mcp` reverse-proxy). Worker is not in this compose. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`. `docker compose up` publishes API `:8080` and web `:3000`. Compose does not terminate TLS; put Caddy/nginx in front for anything beyond localhost, and do not expose `/mcp` without TLS and bearer token discipline. HTTP MCP is a control plane for context and tasks; code tools need a local sidecar, a self-host bind-mount, or an enabled hosted clone.
Self-host Compose is Postgres 16 + API + worker (web/mcp are not in this compose yet). Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`, `BEACON_WORKER_TOKEN`, and `INDEX_RPC_TOKEN`. `docker compose up` publishes API `:8080` so host `/health` and `/ready` work. The worker has no published ports.
Self-host Compose is Postgres 16 + API + web + worker (mcp is not in this compose). Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_TOKEN`, `BEACON_WORKER_TOKEN`, and `INDEX_RPC_TOKEN`. `docker compose up` publishes web `:3000` (same-origin `/v1` rewrite) and API `:8080` so host `/health` still works. Worker index HTTP stays on the Compose network (port 7744, not published).

The sidecar tunnel (`FF_SIDECAR_TUNNEL`, default off) lets `beacon sidecar` dial `wss://$BEACON_HOST/v1/sidecar` with a `code:read` token so remote code tools can reach a laptop index. The CLI dials only when `BEACON_HOST` is set or `FF_SIDECAR_TUNNEL=true`; otherwise heartbeat and local HTTP stay as they are.
Self-host Compose is Postgres 16 + API + worker + web. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`, `BEACON_WORKER_TOKEN`, and `INDEX_RPC_TOKEN`. `docker compose up` publishes web `:3000` (same-origin `/v1` rewrite) and API `:8080`. The worker has no published ports.
Mint a project token with `POST /v1/projects/:id/tokens` (admin session). Settings has no token UI. Then paste it when setup asks:
```bash
setup.cmd     # Windows; double-click so the window stays open
./setup.sh    # Unix
pnpm --filter @beacon/cli start -- setup
pnpm --filter @beacon/cli start -- sidecar
pnpm --filter @beacon/cli start -- mcp
```
`beacon setup` asks for the project token in the console. It writes the local token, a `mcp.cjs` launcher, and Grok / Cursor / Claude MCP configs so those agents can call Beacon. The token stays in `BEACON_HOME`. It does not mint tokens or start a hosted agent. Flags (`--token`, `--project`) still work for non-interactive use.
The living brief is the Context editor. Import an existing `AGENTS.md` only to bootstrap a new project. After that, edit Context and export when a host needs the file. The checked-in `AGENTS.md` is an `exportAgentsMd` snapshot of `packages/context/src/beacon-brief.ts`, not a second source of truth.
