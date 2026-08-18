# ProjectBeacon

TypeScript monorepo (pnpm workspaces + Turborepo).

Application packages live under `apps/` and `packages/`. Follow existing conventions.

## Layout

- `apps/web` — web UI
- `apps/api` — control-plane API
- `apps/mcp` — MCP HTTP transport
- `apps/cli` — CLI + sidecar
- `apps/worker` — background worker
- `packages/db` — database schema
- `packages/api-spec` — OpenAPI / Zod contracts
- `packages/shared` — shared types and helpers
- `packages/context` — project context compiler
- `packages/index-core` — code index
- `packages/mcp-tools` — MCP tool surface
- `packages/ui` — shared UI stub
- `packages/config` — TypeScript, ESLint, and Prettier config

See `docs/design.md` for the system design.
