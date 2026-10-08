namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> MilestoneTools() =>
    [
        new("list_milestones", "List roadmap milestones.",
            () => Props(("projectId", "string", false)), MilestonesList),
        new("get_milestone", "Get one milestone.",
            () => Props(("milestoneId", "string", true)), MilestoneGet),
        new("create_milestone", "Create a roadmap milestone.",
            () => Props(("name", "string", true), ("description", "string", false), ("order", "integer", false),
                ("projectId", "string", false)), MilestoneCreate),
        new("update_milestone", "Update a milestone name, description, or order.",
            () => Props(("milestoneId", "string", true), ("name", "string", false), ("description", "string", false),
                ("order", "integer", false)), MilestoneUpdate),
        new("delete_milestone", "Delete a milestone.",
            () => Props(("milestoneId", "string", true), ("projectId", "string", false)), MilestoneDelete),
        new("close_milestone", "Close a milestone.",
            () => Props(("milestoneId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => MilestoneAction(args, "close", api, env, ct)),
        new("reopen_milestone", "Reopen a milestone.",
            () => Props(("milestoneId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => MilestoneAction(args, "reopen", api, env, ct)),
    ];

    private static async Task<McpToolText> MilestonesList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/milestones", null, ct);
    }

    private static async Task<McpToolText> MilestoneGet(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/milestones/{milestoneId:D}", null, ct);
    }

    private static async Task<McpToolText> MilestoneCreate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var name = Text(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return new McpToolText(true, "missing 'name'");
        var body = new JsonObject
        {
            ["name"] = name,
            ["projectId"] = projectId.ToString("D"),
            ["order"] = IntArg(args, "order") ?? 0
        };
        Put(body, "description", Text(args, "description"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/milestones", body, ct);
    }

    private static async Task<McpToolText> MilestoneUpdate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        var body = new JsonObject { ["milestoneId"] = milestoneId.ToString("D") };
        Put(body, "name", Text(args, "name"));
        Put(body, "description", Text(args, "description"));
        var order = IntArg(args, "order");
        if (order is not null)
            body["order"] = order.Value;
        return await SendAsync(api, HttpMethod.Put, $"v1/milestones/{milestoneId:D}", body, ct);
    }

    private static async Task<McpToolText> MilestoneDelete(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Delete, $"v1/milestones/{milestoneId:D}", null, ct);
        return await SendAsync(api, HttpMethod.Delete, $"v1/projects/{projectId:D}/milestones/{milestoneId:D}", null, ct);
    }

    private static async Task<McpToolText> MilestoneAction(JsonObject? args, string action, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/milestones/{milestoneId:D}/{action}", new JsonObject(), ct);
    }

}