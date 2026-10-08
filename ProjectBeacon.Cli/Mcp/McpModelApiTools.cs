namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> ModelTools() =>
    [
        new("model_upsert", "Create or update a local model backend. backendType: FreeToken, LlamaCpp, OpenAiCompatible.",
            () => Props(("name", "string", true), ("backendType", "string", true), ("launchCommand", "string", true),
                ("contextSize", "integer", true), ("ttl", "integer", true), ("id", "string", false),
                ("extraFlags", "array", false), ("concurrent", "boolean", false)), ModelUpsert),
        new("model_delete", "Delete a local model backend.",
            () => Props(("modelBackendId", "string", true)), ModelDelete),
        new("model_unbind", "Remove a pipeline role binding. role: planner, actor, review.",
            () => Props(("role", "string", true)), ModelUnbind),
        new("proxy_reload", "Ask the workstation to reload llama-swap.",
            () => Props(), (_, api, _, ct) => SendAsync(api, HttpMethod.Post, "v1/models/proxy/reload", new JsonObject(), ct)),
        new("proxy_unload", "Ask the workstation to unload llama-swap.",
            () => Props(), (_, api, _, ct) => SendAsync(api, HttpMethod.Post, "v1/models/proxy/unload", new JsonObject(), ct))
    ];

    private static async Task<McpToolText> ModelUpsert(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        var name = Text(args, "name");
        var backendType = Text(args, "backendType");
        var launchCommand = Text(args, "launchCommand");
        var contextSize = IntArg(args, "contextSize");
        var ttl = IntArg(args, "ttl");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(backendType) || string.IsNullOrWhiteSpace(launchCommand)
            || contextSize is null || ttl is null)
            return new McpToolText(true, "name, backendType, launchCommand, contextSize, and ttl are required");
        var body = new JsonObject
        {
            ["name"] = name,
            ["backendType"] = backendType,
            ["launchCommand"] = launchCommand,
            ["contextSize"] = contextSize.Value,
            ["ttl"] = ttl.Value,
            ["concurrent"] = Flag(args, "concurrent") ?? false,
            ["extraFlags"] = StringArray(Strings(args, "extraFlags"))
        };
        Put(body, "id", Text(args, "id"));
        return await SendAsync(api, HttpMethod.Post, "v1/models", body, ct);
    }

    private static async Task<McpToolText> ModelDelete(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "modelBackendId", out var id))
            return new McpToolText(true, "missing or invalid 'modelBackendId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/models/{id:D}", null, ct);
    }

    private static async Task<McpToolText> ModelUnbind(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        var role = RoleName(Text(args, "role"));
        if (role is null)
            return new McpToolText(true, "missing or invalid 'role' (expected planner, actor or review)");
        return await SendAsync(api, HttpMethod.Delete, $"v1/models/bind/{role}", null, ct);
    }

}