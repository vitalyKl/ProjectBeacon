# ProjectBeacon

Project operating system for mixed human + AI-agent development.

TypeScript monorepo (pnpm workspaces + Turborepo). The web app (`apps/web`) is a Next.js App Router app with auth pages, a context editor, and a same-origin `/v1` rewrite. See [docs/design.md](docs/design.md). The product position — why Beacon vs Linear/Jira+MCP and vs editor AGENTS.md — is in [docs/positioning.md](docs/positioning.md). The living brief is Context; [AGENTS.md](AGENTS.md) is an export for hosts that only read the repo.

## Prerequisites

- Node.js 22+ or 24 LTS
- [pnpm](https://pnpm.io) 10

## Setup

```bash
pnpm install
pnpm typecheck
```

Workspace scripts: `pnpm build`, `pnpm lint`, `pnpm test`, `pnpm dev`.

Self-host Compose is Postgres 16 + API + web + worker. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD`, `BEACON_WORKER_TOKEN`, and `INDEX_RPC_TOKEN`. `docker compose up` publishes web `:3000` (same-origin `/v1` rewrite) and API `:8080`. Worker index HTTP stays on the Compose network (port 7744, not published). `apps/mcp` HTTP is a stub; local agents use `beacon mcp` (stdio).

Mint a project token from Agents or `POST /v1/projects/:id/tokens` (admin session). Then paste it when setup asks:

```bash
setup.cmd     # Windows; double-click so the window stays open
./setup.sh    # Unix
pnpm --filter @beacon/cli start -- setup
pnpm --filter @beacon/cli start -- sidecar
pnpm --filter @beacon/cli start -- mcp
```

`beacon setup` asks for the project token in the console. It writes `BEACON_HOME/config.toml`, a local `mcp.cjs` launcher, and Grok / Cursor / Claude MCP configs so those agents can call Beacon. The token stays in `BEACON_HOME`. It does not mint tokens or start a hosted agent.

The living brief is the Context editor. Import an existing `AGENTS.md` only to bootstrap a new project. After that, edit Context and export when a host needs the file.
