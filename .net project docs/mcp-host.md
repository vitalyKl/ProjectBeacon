# Beacon MCP host (stdio)

Local agents talk to Beacon file tools over stdio. HTTP MCP is not shipped.

```
dotnet run --project ProjectBeacon.Cli -- mcp --root <project-root>
```

Or after publish, `beacon mcp --root <project-root>`.

`BEACON_PROJECT_ROOT` overrides the working directory when `--root` is omitted.

## Tools

- `read_file` `{ path }`
- `write_file` `{ path, content }`
- `apply_patch` `{ path, oldText, newText }` — one exact occurrence; otherwise error, no partial write

Paths are resolved inside the project root. `../` and other escapes are rejected. There is no shell tool.

## Deny native file tools on the host

OpenCode / comparable harness:

```json
{
  "permission": {
    "read": "deny",
    "edit": "deny",
    "glob": "deny",
    "grep": "deny"
  }
}
```

Grok (`~/.grok/config.toml`), stdio only:

```toml
[mcp_servers.beacon]
command = "dotnet"
args = ["run", "--project", "ProjectBeacon.Cli", "--", "mcp", "--root", "."]
enabled = true
```

Do not add an HTTP/SSE Beacon MCP server.
