namespace ProjectBeacon.Application.Agents;

using System.Text.Json.Nodes;

public record CustomMcpServer(string Name, string Type, string? Command, string? Url, bool Enabled = true);

public static class McpCatalog
{
    public static JsonObject Build(
        string localRoot,
        bool context7,
        bool serena,
        IEnumerable<CustomMcpServer>? custom = null)
    {
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
        {
            mcp["context7"] = new JsonObject
            {
                ["type"] = "remote",
                ["url"] = "https://mcp.context7.com/mcp",
                ["enabled"] = true
            };
        }
        if (serena)
        {
            mcp["serena"] = new JsonObject
            {
                ["type"] = "local",
                ["command"] = new JsonArray("serena", "start-mcp-server"),
                ["enabled"] = true
            };
        }
        if (custom is not null)
        {
            foreach (var server in custom)
            {
                var name = server.Name.Trim();
                if (name.Length == 0 || name.Equals("beacon", StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = new JsonObject { ["type"] = server.Type, ["enabled"] = server.Enabled };
                if (string.Equals(server.Type, "remote", StringComparison.OrdinalIgnoreCase))
                    node["url"] = server.Url ?? "";
                else
                {
                    var parts = (server.Command ?? "")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var args = new JsonArray();
                    foreach (var part in parts)
                        args.Add(part);
                    node["command"] = args;
                }
                mcp[name] = node;
            }
        }
        return mcp;
    }
}
