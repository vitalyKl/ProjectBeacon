namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal readonly record struct McpToolText(bool IsError, string Text);

internal static partial class McpApiTools
{
    private const string MissingApi =
        "BEACON_API_URL and BEACON_API_TOKEN are required. These tools call the Beacon /v1 API.";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private sealed record McpTool(
        string Name,
        string Description,
        Func<JsonObject> Schema,
        Func<JsonObject?, BeaconApiClient, McpEnvironment, CancellationToken, Task<McpToolText>> Handler);

    private static readonly McpTool[] Tools =
    [
        ..TaskTools(),
        ..ContextTools(),
        ..GovernTools(),
        ..MilestoneTools(),
        ..CatalogTools(),
        ..PipelineTools(),
        ..ModelTools(),
    ];

    private static readonly HashSet<string> Names =
        Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

    public static bool IsApiTool(string name) => Names.Contains(name);

    public static IEnumerable<JsonObject> Definitions()
    {
        foreach (var tool in Tools)
            yield return Tool(tool.Name, tool.Description, tool.Schema());
    }

    public static Task<McpToolText> CallAsync(string name, JsonObject? args, BeaconApiClient? api, McpEnvironment env, CancellationToken ct = default)
    {
        if (api is null)
            return Task.FromResult(new McpToolText(true, MissingApi));
        return InvokeAsync(name, args, api, env, ct);
    }

    public static Task<McpToolText> BindRoleAsync(PipelineRole role, Guid modelBackendId, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["role"] = role.ToString(),
            ["modelBackendId"] = modelBackendId.ToString("D")
        };
        return SendAsync(api, HttpMethod.Post, "v1/models/bind", body, ct);
    }

    public static async Task<McpToolText> ModelStatusAsync(BeaconApiClient api, CancellationToken ct = default)
    {
        var registry = await SendRawAsync(api, HttpMethod.Get, "v1/models", null, ct);
        if (registry.IsError)
            return registry;
        var proxy = await SendRawAsync(api, HttpMethod.Get, "v1/models/proxy/status", null, ct);
        JsonNode? registryNode;
        try
        {
            registryNode = JsonNode.Parse(registry.Text);
        }
        catch (JsonException)
        {
            return registry;
        }

        if (registryNode is not JsonObject obj)
            return registry;

        if (proxy.IsError)
            obj["proxy"] = new JsonObject { ["available"] = false, ["error"] = proxy.Text };
        else
        {
            try
            {
                obj["proxy"] = JsonNode.Parse(proxy.Text);
            }
            catch (JsonException)
            {
                obj["proxy"] = proxy.Text;
            }
        }

        return new McpToolText(false, obj.ToJsonString(Pretty));
    }

    public static Task<McpToolText> CreateSubtaskAsync(
        Guid taskId,
        string instructions,
        IReadOnlyList<string> allowedMcpTools,
        IReadOnlyList<string> allowedPaths,
        BeaconApiClient api,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["instructions"] = instructions,
            ["allowedMcpTools"] = StringArray(allowedMcpTools),
            ["allowedPaths"] = StringArray(allowedPaths)
        };
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks", body, ct);
    }

    public static Task<McpToolText> ReportResultAsync(
        Guid taskId, Guid subtaskId, string diffRef, string summary, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["subtaskId"] = subtaskId.ToString("D"),
            ["diffRef"] = diffRef,
            ["summary"] = summary
        };
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/result", body, ct);
    }

    public static Task<McpToolText> ReviewVerdictAsync(
        Guid taskId, ReviewVerdictKind kind, string note, Guid? subtaskId, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["kind"] = kind.ToString(),
            ["note"] = note
        };
        if (subtaskId is not null)
            body["subtaskId"] = subtaskId.Value.ToString("D");
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/verdict", body, ct);
    }

    public static Task<McpToolText> PipelineStatusAsync(Guid taskId, BeaconApiClient api, CancellationToken ct = default)
        => SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/pipeline", null, ct);

    private static async Task<McpToolText> InvokeAsync(string name, JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        try
        {
            var tool = Tools.FirstOrDefault(t => t.Name == name);
            if (tool is null)
                return new McpToolText(true, $"Unknown tool: {name}");
            return await tool.Handler(args, api, env, ct);
        }
        catch (Exception ex)
        {
            return new McpToolText(true, ex.Message);
        }
    }
}