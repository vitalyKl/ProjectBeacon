# ProjectBeacon

Project operating system for mixed human + AI-agent development.

TypeScript monorepo (pnpm workspaces + Turborepo). The web shell (`apps/web`) is a Next.js App Router app with auth pages and a same-origin `/v1` rewrite. See [docs/design.md](docs/design.md).

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
