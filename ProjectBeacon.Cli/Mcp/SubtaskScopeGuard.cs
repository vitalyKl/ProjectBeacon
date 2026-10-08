namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
/// <summary>
/// Enforces a subtask allowlist on MCP tools. An empty tool list does not restrict tools. An empty path list does not restrict paths. Several limited in-progress subtasks require an explicit subtask id.
/// </summary>
public static class SubtaskScopeGuard
{
    /// <summary>
    /// One subtask's id, status, allowed MCP tools, and allowed path prefixes.
    /// </summary>
    public readonly record struct Allowlist(Guid Id, string Status, IReadOnlyList<string> Tools, IReadOnlyList<string> Paths);

    private static readonly HashSet<string> PathTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "apply_patch", "get_tree", "search_code", "get_changed_scope", "get_signatures", "get_callers", "hash_range"
    };

    public static bool UsesPath(string tool) => PathTools.Contains(tool);

    public static IReadOnlyList<Allowlist> ParsePipeline(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subtasks", out var subtasks) || subtasks.ValueKind != JsonValueKind.Array)
                return [];

            var lists = new List<Allowlist>();
            foreach (var item in subtasks.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement) || !Guid.TryParse(idElement.GetString(), out var id))
                    continue;
                var status = item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? ""
                    : "";
                lists.Add(new Allowlist(id, status, ReadStrings(item, "allowedMcpTools"), ReadStrings(item, "allowedPaths")));
            }
            return lists;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Reject(string tool, IReadOnlyList<string>? requestedPaths, IReadOnlyList<Allowlist> subtasks, Guid? explicitSubtaskId)
    {
        var active = Resolve(subtasks, explicitSubtaskId, out var error);
        if (error is not null)
            return error;
        if (active is null)
            return null;

        if (active.Value.Tools.Count > 0 && !ToolAllowed(active.Value.Tools, tool))
            return $"tool '{tool}' is not allowed for this subtask";

        if (active.Value.Paths.Count == 0 || requestedPaths is null)
            return null;

        if (requestedPaths.Count == 0)
            return "path is outside the subtask allowlist";

        foreach (var path in requestedPaths)
        {
            if (!PathAllowed(active.Value.Paths, path))
                return $"path '{path}' is outside the subtask allowlist";
        }
        return null;
    }

    private static Allowlist? Resolve(IReadOnlyList<Allowlist> subtasks, Guid? explicitSubtaskId, out string? error)
    {
        error = null;
        if (explicitSubtaskId is Guid id)
        {
            var named = subtasks.FirstOrDefault(s => s.Id == id);
            if (named.Id == Guid.Empty)
            {
                error = "subtask not found";
                return null;
            }
            return named.Tools.Count == 0 && named.Paths.Count == 0 ? null : named;
        }

        var restricted = subtasks.Where(InProgress).Where(s => s.Tools.Count > 0 || s.Paths.Count > 0).ToList();
        if (restricted.Count == 1)
            return restricted[0];
        if (restricted.Count > 1)
        {
            error = "multiple in-progress subtasks have tool or path limits; pass subtaskId";
            return null;
        }
        return null;
    }

    private static bool InProgress(Allowlist subtask)
        => string.Equals(subtask.Status, "InProgress", StringComparison.OrdinalIgnoreCase);

    private static bool ToolAllowed(IReadOnlyList<string> tools, string tool)
        => tools.Any(entry =>
            string.Equals(entry, tool, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry, "beacon_" + tool, StringComparison.OrdinalIgnoreCase));

    private static bool PathAllowed(IReadOnlyList<string> prefixes, string path)
    {
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
            return false;
        return prefixes.Any(prefix =>
        {
            var root = prefix.Replace('\\', '/').Trim().Trim('/');
            return normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString() ?? "")
            .Where(entry => entry.Length > 0)
            .ToList();
    }
}
