namespace ProjectBeacon.Application.Agents;

using System.Text.Json.Nodes;

/// <summary>Typed local MCP process configuration. Arguments are preserved exactly; no shell splitting occurs.</summary>
public sealed record LocalMcpServer(string Name, string Executable, IReadOnlyList<string> Arguments, bool Enabled = true);

/// <summary>Typed remote MCP endpoint configuration.</summary>
public sealed record RemoteMcpServer(string Name, Uri Endpoint, bool Enabled = true);

/// <summary>
/// Compatibility input for existing persisted custom MCP settings. New callers should use
/// <see cref="LocalMcpServer"/> or <see cref="RemoteMcpServer"/> to preserve argument boundaries.
/// </summary>
[Obsolete("Use LocalMcpServer or RemoteMcpServer. The legacy command string cannot preserve argument boundaries.")]
public record CustomMcpServer(string Name, string Type, string? Command, string? Url, bool Enabled = true);

/// <summary>Builds the OpenCode MCP block using validated, typed server definitions.</summary>
public static class McpCatalog
{
    public static JsonObject Build(
        string localRoot,
        bool context7,
        bool serena,
        IEnumerable<CustomMcpServer>? custom = null) =>
        BuildTyped(localRoot, context7, serena,
            (custom ?? []).Select(FromLegacy).Where(server => server is not null).Cast<object>());

    public static JsonObject BuildTyped(
        string localRoot,
        bool context7,
        bool serena,
        IEnumerable<object>? custom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        var mcp = new JsonObject
        {
            ["beacon"] = new JsonObject
            {
                ["type"] = "local",
                ["command"] = new JsonArray("beacon", "mcp", "--root", localRoot),
                ["enabled"] = true
            }
        };
        if (context7)
            mcp["context7"] = RemoteNode(new RemoteMcpServer("context7", new Uri("https://mcp.context7.com/mcp")));
        if (serena)
            mcp["serena"] = LocalNode(new LocalMcpServer("serena", "serena", ["start-mcp-server"]));

        if (custom is null)
            return mcp;

        foreach (var server in custom)
        {
            var name = server switch
            {
                LocalMcpServer local => local.Name,
                RemoteMcpServer remote => remote.Name,
                _ => throw new ArgumentException($"Unsupported MCP server descriptor '{server.GetType().Name}'.", nameof(custom))
            };
            name = ValidateName(name);
            if (name.Equals("beacon", StringComparison.OrdinalIgnoreCase))
                continue;

            var node = server switch
            {
                LocalMcpServer local => LocalNode(local),
                RemoteMcpServer remote => RemoteNode(remote),
                _ => throw new InvalidOperationException("Unreachable MCP descriptor type.")
            };
            mcp[name] = node;
        }

        return mcp;
    }

    private static object? FromLegacy(CustomMcpServer server)
    {
        var name = ValidateName(server.Name);
        if (name.Equals("beacon", StringComparison.OrdinalIgnoreCase))
            return null;
        if (server.Type.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException($"Remote MCP server '{name}' requires an absolute HTTP(S) URL.");
            return new RemoteMcpServer(name, endpoint, server.Enabled);
        }

        var command = (server.Command ?? string.Empty).Trim();
        if (command.Length == 0)
            throw new ArgumentException($"Local MCP server '{name}' requires an executable.");
        // Backwards-compatible parsing is intentionally conservative. It cannot reliably parse
        // shell quoting; callers needing spaces or special characters must migrate to typed args.
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LocalMcpServer(name, parts[0], parts.Skip(1).ToArray(), server.Enabled);
    }

    private static JsonObject LocalNode(LocalMcpServer server)
    {
        var executable = server.Executable?.Trim() ?? string.Empty;
        if (executable.Length == 0)
            throw new ArgumentException($"Local MCP server '{server.Name}' requires an executable.");
        var args = new JsonArray();
        foreach (var arg in server.Arguments)
            args.Add(arg);
        return new JsonObject { ["type"] = "local", ["command"] = new JsonArray(new JsonNode?[] { JsonValue.Create(executable) }.Concat(args.Select(node => node?.DeepClone())).ToArray()), ["enabled"] = server.Enabled };
    }

    private static JsonObject RemoteNode(RemoteMcpServer server)
    {
        if (!server.Endpoint.IsAbsoluteUri || server.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Remote MCP server '{server.Name}' requires an absolute HTTP(S) URL.");
        return new JsonObject { ["type"] = "remote", ["url"] = server.Endpoint.AbsoluteUri, ["enabled"] = server.Enabled };
    }

    private static string ValidateName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
            throw new ArgumentException("MCP server name must contain 1-100 printable characters.");
        return name;
    }
}
