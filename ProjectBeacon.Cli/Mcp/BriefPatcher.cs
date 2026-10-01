namespace ProjectBeacon.Cli.Mcp;

using Application.CodeIndex;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class BriefPatcher
{
    private static readonly JsonSerializerOptions JsonCompact = new() { WriteIndented = false };

    internal static async Task<McpToolText> PatchCompileAsync(
        string projectId, string? taskId, CodeIndex index, BeaconApiClient api, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject
            {
                ["includeHandoff"] = false,
                ["includeChangedScope"] = false,
                ["includeTreeCapsule"] = false
            };
            var prefix = await LabelPrefixAsync(api, projectId, taskId, ct);
            var prefixes = prefix is null ? null : new[] { prefix };
            var tree = index.GetTree(prefix);
            if (tree.Success && tree.Value is not null)
            {
                body["includeTreeCapsule"] = true;
                body["treeCapsule"] = FormatTree(tree.Value);
            }
            var changed = index.GetChangedScope(prefixes);
            if (changed.Success && changed.Value is not null)
            {
                body["includeChangedScope"] = true;
                body["changedScope"] = FormatFileList(changed.Value);
            }
            if (taskId is not null)
                body["taskId"] = taskId;

            var result = await api.SendAsync(HttpMethod.Post, $"v1/projects/{projectId}/context/compile", body, ct);
            if (!result.Ok)
                return new McpToolText(true, $"API error: {result.Status}: {result.Body}");

            if (ExtractBriefMarkdown(result.Body) is null)
                return new McpToolText(true, "Could not extract brief markdown from API response.");

            return new McpToolText(false, result.Body);
        }
        catch (Exception ex)
        {
            return new McpToolText(true, $"Error: {ex.Message}");
        }
    }

    internal static string? ExtractBriefMarkdown(string rawJson)
    {
        try
        {
            var json = JsonNode.Parse(rawJson);
            if (json is null)
                return null;

            // { success: true, value: { briefMarkdown: "..." } }
            if (json["value"] is JsonObject valueObj)
            {
                if (valueObj["briefMarkdown"] is JsonValue v && v.TryGetValue<string>(out var m))
                    return m;
                if (valueObj["value"] is JsonObject nested
                    && nested["briefMarkdown"] is JsonValue nv && nv.TryGetValue<string>(out var nm))
                    return nm;
            }

            // { briefMarkdown: "..." }
            if (json is JsonObject root && root["briefMarkdown"] is JsonValue rv && rv.TryGetValue<string>(out var rm))
                return rm;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> LabelPrefixAsync(BeaconApiClient api, string projectId, string? taskId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return null;
        var task = await api.SendAsync(HttpMethod.Get, $"v1/tasks/{taskId}", null, ct);
        if (!task.Ok)
            return null;
        var labelId = ReadString(task.Body, "labelId");
        if (string.IsNullOrWhiteSpace(labelId))
            return null;
        var labels = await api.SendAsync(HttpMethod.Get, $"v1/projects/{projectId}/labels", null, ct);
        if (!labels.Ok)
            return null;
        try
        {
            var node = JsonNode.Parse(labels.Body);
            var array = node switch
            {
                JsonArray direct => direct,
                JsonObject obj when obj["value"] is JsonArray value => value,
                _ => null
            };
            if (array is null)
                return null;
            foreach (var item in array)
            {
                if (item is not JsonObject label)
                    continue;
                var id = label["id"]?.ToString();
                if (!string.Equals(id, labelId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var prefix = label["pathPrefix"]?.ToString();
                return string.IsNullOrWhiteSpace(prefix) ? null : prefix;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node?[name] is JsonValue direct && direct.TryGetValue<string>(out var value))
                return value;
            if (node?["value"]?[name] is JsonValue nested && nested.TryGetValue<string>(out var nestedValue))
                return nestedValue;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static string FormatTree(TreeResult tree)
    {
        var suffix = tree.Truncated ? ", truncated" : "";
        var lines = new List<string> { $"# Tree ({tree.Entries.Count} entries{suffix})" };
        foreach (var entry in tree.Entries)
        {
            var depth = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var name = entry.Path.EndsWith('/') ? entry.Path[..^1] : entry.Path;
            var fileName = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
            lines.Add(new string(' ', depth * 2) + (entry.IsDirectory ? fileName + "/" : fileName));
        }
        return string.Join("\n", lines);
    }

    internal static string FormatFileList(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "No changed files.";
        return string.Join("\n", files);
    }
}
