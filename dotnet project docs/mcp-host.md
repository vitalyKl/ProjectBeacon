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
- `get_tree` `{ path?, maxEntries? }` — re-scans the working tree; skips `node_modules`, `.git`, `bin`, `obj`, etc.
- `search_code` `{ query, path?, maxMatches? }` — literal substring search over text files, case-insensitive
- `get_changed_scope` `{ path?, maxFiles? }` — changed files from `git status`; the root must be a git repository

For `search_code` and `get_changed_scope`, `path` is a comma-separated list of path prefixes; an empty or omitted value means the whole tree.

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
