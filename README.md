# ProjectBeacon

.NET 9 project operating system for mixed human + agent development. Blazor Server + MudBlazor on `:5083`. Agents use stdio `beacon mcp`, not HTTP MCP.

Authoritative product docs: `.net project docs/ProjectBeacon-design-doc-v2.md` (wins on conflict) and `ProjectBeacon-dotnet-roadmap-v2.md`. Process contract: `AGENTS.md`.

## Self-host

1. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` and/or `ConnectionStrings__Default`, `BOOTSTRAP_ADMIN_TOKEN`, and `JWT__Secret`.
2. Start Postgres: `docker compose up -d` (or let `DockerPostgresHelper` start `beacon-postgres` on first Web run).
3. `dotnet run --project ProjectBeacon.Web --launch-profile http` — Web loads `.env` from the repo root.
4. Open `http://localhost:5083/bootstrap`, then `/login`.

Drawer: Dashboard, Board, Backlog, Roadmap, Context, Decisions, Agents, Reports, Settings. Language is a cookie (`GET /culture`), not custom JS. UI screens live in `ProjectBeacon.Web/Features/` and call Application handlers.

## Agent MCP

```
dotnet run --project ProjectBeacon.Cli -- mcp --root .
```

See `.net project docs/mcp-host.md`. Hosted clone, outbound WSS, HTTP MCP, and `write_handoff` are not shipped.
