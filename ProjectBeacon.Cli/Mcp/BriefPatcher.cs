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
            if (taskId is not null)
                body["taskId"] = taskId;

            var result = await api.SendAsync(HttpMethod.Post, $"v1/projects/{projectId}/context/compile", body, ct);
            if (!result.Ok)
                return new McpToolText(true, $"API error: {result.Status}: {result.Body}");

            var briefMarkdown = ExtractBriefMarkdown(result.Body);
            if (briefMarkdown is null)
                return new McpToolText(true, "Could not extract brief markdown from API response.");

            var patched = PatchBriefMarkdown(briefMarkdown, index);
            return new McpToolText(false, patched);
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

    internal static string PatchBriefMarkdown(string briefMarkdown, CodeIndex index)
    {
        var patched = ReplaceTreeSection(briefMarkdown, index);
        patched = ReplaceScopeSection(patched, index);
        return patched;
    }

    private static string ReplaceTreeSection(string text, CodeIndex index)
    {
        var headerIdx = text.IndexOf("## Tree", StringComparison.Ordinal);
        if (headerIdx < 0)
            return text;

        var headerEnd = headerIdx + "## Tree".Length;
        var endOfSection = FindSectionEnd(text, headerEnd);
        var treeResult = index.GetTree();
        if (!treeResult.Success || treeResult.Value is null)
            return text;

        var newContent = FormatTree(treeResult.Value);
        // Keep header, replace content up to next section, add newline separator
        return text[..headerEnd] + "\n\n" + newContent + text[endOfSection..];
    }

    private static string ReplaceScopeSection(string text, CodeIndex index)
    {
        var headerIdx = text.IndexOf("## Changed scope", StringComparison.Ordinal);
        if (headerIdx < 0)
            return text;

        var headerEnd = headerIdx + "## Changed scope".Length;
        var endOfSection = FindSectionEnd(text, headerEnd);
        var changedResult = index.GetChangedScope();
        if (!changedResult.Success)
            return text;

        var newContent = FormatFileList(changedResult.Value!);
        return text[..headerEnd] + "\n\n" + newContent + text[endOfSection..];
    }

    private static int FindSectionEnd(string text, int contentStart)
    {
        // contentStart is right after the header, e.g. in "...## Tree\n\nstub..."
        // it points to the first \n. We need to find the NEXT line starting with ##
        var searchFrom = contentStart + 1;
        while (searchFrom < text.Length)
        {
            var nl = text.IndexOf('\n', searchFrom);
            if (nl < 0)
                return text.Length;

            var nextLineStart = nl + 1;
            if (nextLineStart + 1 < text.Length && text[nextLineStart] == '#' && text[nextLineStart + 1] == '#')
                return nextLineStart;

            searchFrom = nextLineStart;
        }
        return text.Length;
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
