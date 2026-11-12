namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;

/// <summary>Centralized policy decisions for native tool exposure, authorization, and caching.</summary>
internal static class NativeAgentToolPolicy
{
    public static bool IsPlanToolAllowed(string name) =>
        PipelineToolPolicy.InteractivePlanTools.Contains(name, StringComparer.Ordinal);

    public static bool IsPlanModeCommandExecutionBlocked(string agentMode, string? toolName)
        => string.Equals(agentMode, "plan", StringComparison.Ordinal)
            && toolName is "run_command" or "service_start" or "service_stop";

    public static JsonArray FilterPlanTools(JsonArray tools) =>
        FilterToolsByPolicy(tools, PipelineToolPolicy.InteractivePlanTools, allowList: true);

    public static JsonArray FilterDeniedTools(JsonArray tools, IReadOnlyList<string> deniedTools) =>
        FilterToolsByPolicy(tools, deniedTools, allowList: false);

    private static JsonArray FilterToolsByPolicy(JsonArray tools, IReadOnlyList<string> names, bool allowList)
    {
        var selected = names.ToHashSet(StringComparer.Ordinal);
        var filtered = new JsonArray();
        foreach (var tool in tools)
        {
            var name = tool?["function"]?["name"]?.GetValue<string>();
            if (name is not null && (selected.Contains(name) == allowList))
                filtered.Add(tool!.DeepClone());
        }
        return filtered;
    }

    public static bool IsSuccessfulTerminalTool(string? role, string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(toolName)) return false;
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
            return toolName is McpToolNames.SubtaskReportResult or McpToolNames.PipelineFailSubtask;
        if (string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase))
            return toolName == McpToolNames.PipelineStartReview;
        if (!string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase)) return false;
        if (toolName == McpToolNames.PipelineApprove) return true;
        if (toolName != McpToolNames.TaskReviewVerdict || string.IsNullOrWhiteSpace(argumentsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
            var normalized = new string((kind.GetString() ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            return string.Equals(normalized, "reopensubtask", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    public static bool IsAdvertisedMcpTool(JsonArray tools, string name)
    {
        foreach (var tool in tools)
        {
            if (string.Equals(
                tool?["function"]?["name"]?.GetValue<string>(),
                name,
                StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> CacheableReadOnlyTools = new(StringComparer.Ordinal)
    {
        McpToolNames.ReadFile, "list_files", McpToolNames.GetProject, "list_projects", McpToolNames.GetTask, McpToolNames.ListTasks,
        McpToolNames.GetMilestone, McpToolNames.ListMilestones, "get_label", McpToolNames.ListLabels, McpToolNames.ContextCompile,
        "context_get", McpToolNames.SearchCode
    };

    internal static bool MayRequestUserInput(string? tool)
        => string.Equals(tool, "suggest_command_prefixes", StringComparison.Ordinal)
            || string.Equals(tool, "run_command", StringComparison.Ordinal)
            || string.Equals(tool, "service_start", StringComparison.Ordinal);

    public static bool IsCacheableTool(string? name)
        => name is not null && CacheableReadOnlyTools.Contains(name);

    internal static string CreateToolCacheKey(string tool, JsonObject arguments)
    {
        var canonical = CanonicalizeJson(arguments);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(tool + "\0" + canonical)));
    }

    private static string CanonicalizeJson(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalizeJson(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(CanonicalizeJson)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };

    internal static string CreateToolCallFingerprint(IEnumerable<NativeToolCallAccumulator> calls)
    {
        var signature = string.Join("\n", calls
            .OrderBy(call => call.Index)
            .Select(call => $"{call.Name ?? "unknown_tool"}\0{call.Arguments}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
    }

    public static bool IsInteractiveChatAuthorization(AgentHarnessAuthorization authorization)
        => authorization.PipelineRole is null
            && authorization.TaskId is null
            && authorization.SubtaskId is null;

    public static bool IsRunCommandAuthorized(AgentHarnessAuthorization authorization)
    {
        if (!authorization.CommandsEnabled)
            return false;

        // Command execution is never advertised outside an isolated worktree. Interactive
        // sessions are governed by workstation policy; only Actor pipeline sessions may execute.
        if (!authorization.IsolatedWorkspace)
            return false;
        if (IsInteractiveChatAuthorization(authorization))
            return true;
        return string.Equals(authorization.PipelineRole, "Actor", StringComparison.OrdinalIgnoreCase)
            && authorization.TaskId is not null
            && authorization.SubtaskId is not null;
    }

}
