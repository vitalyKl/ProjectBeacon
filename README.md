# ProjectBeacon

.NET 9 project operating system for mixed human + agent development. Blazor Server + MudBlazor on `:5083`. Agents use stdio `beacon mcp`, not HTTP MCP. The workstation client (`beacon client`) talks outbound HTTPS to the control plane; the Web host does not read the user's disk.

Authoritative product docs: `dotnet project docs/ProjectBeacon-design-doc-v2.md` (wins on conflict) and `ProjectBeacon-dotnet-roadmap-v2.md`. Process contract: `AGENTS.md`.

## Self-host

1. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` and/or `ConnectionStrings__Default`, `BOOTSTRAP_ADMIN_TOKEN`, and `JWT__Secret`.
2. Start Postgres: `docker compose up -d` (or let `DockerPostgresHelper` start `beacon-postgres` on first Web run).
3. `dotnet run --project ProjectBeacon.Web --launch-profile http` — Web loads `.env` from the repo root.
4. Open `http://localhost:5083/` (landing). Bootstrap at `/bootstrap`, then `/login`. Logged-in `/` goes to the dashboard.
5. Users: `/register` is open unless `AUTH_LOCAL_INVITE_ONLY=true` (default in `.env.example`). Invite from the open project on the dashboard (email + role); the `bci_` link is shown once and emailed when `MAIL__*` is set. Forgot password is `/forgot` → `/reset`. `/recover` is bootstrap-token admin break-glass only.
6. Product version lives in `Directory.Build.props`. `GET /v1/version` returns `{ version, gitSha }`. The drawer footer shows the assembly version.

Drawer: Dashboard, Board, Backlog, Roadmap, Context, Decisions, Agents, Chat, Reports, Settings. Language is a cookie (`GET /culture`), not custom JS. UI screens live in `ProjectBeacon.Web/Features/` and call Application handlers.

## Workstation client

The Web UI never browses the developer machine. A local client enrolls, heartbeats, and runs commands (init repo, OpenCode config, llama-swap, install).

1. In a terminal, run the first-run walkthrough (URL, sign-in, paths, probe/install, Windows autostart):
   ```
   dotnet run --project ProjectBeacon.Cli -- client
   ```
   Keep that window open. Keys: **S** settings, **P** probe, **R**/**U** llama-swap reload/unload, **A** autostart, **L** log, **O** open Web, **Q** quit.
2. Non-interactive enroll (CI / `--headless`):
   ```
   dotnet run --project ProjectBeacon.Cli -- client enroll --url http://localhost:5083 --login <user> --password <pass>
   dotnet run --project ProjectBeacon.Cli -- client --url http://localhost:5083 --token <bcd_…> --headless
   ```
   Or mint a device in Settings and paste the `bcd_` token (shown once) into the walkthrough or `--token`.
3. Create a project (`/projects/new`), pick the online device, browse its disk, optionally initialize `.gitignore` + `opencode.json`.
4. Agents: Solo/Pipeline models and MCP catalog apply on the selected device. Settings: models/history paths and winget install (git/node/docker) after Confirm. Mark a backend **Load with others** so llama-swap keeps it resident (`groups.resident`). Agents and Dashboard show CPU/RAM/GPU from the client heartbeat.

llama-swap is started by the client (`GET /v1/devices/me/llamaswap-config`), not by the Web process. Concurrent backends must use `${PORT}` in the launch command.

## Agent MCP

```
dotnet run --project ProjectBeacon.Cli -- mcp --root .
```

See `dotnet project docs/mcp-host.md`. Hosted clone, outbound WSS, HTTP MCP, and `write_handoff` are not shipped.

## Kubernetes

Blue-green for the Web host: `deploy/README.md`. Tag `v*` builds `ghcr.io/<org>/projectbeacon-web` with `BEACON_VERSION` from the tag (`v1.2.3` → `1.2.3`) and `BEACON_GIT_SHA`. `beacon client` is not auto-updated.
