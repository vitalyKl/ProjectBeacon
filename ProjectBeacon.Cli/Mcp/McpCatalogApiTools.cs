namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> CatalogTools() =>
    [
        new("list_labels", "List project area labels.",
            () => Props(("projectId", "string", false)), LabelsList),
        new("match_label", "Match a file path to an active label.",
            () => Props(("path", "string", true), ("projectId", "string", false)), LabelMatch),
        new("add_label_path", "Add a path prefix to a label.",
            () => Props(("labelId", "string", true), ("path", "string", true), ("projectId", "string", false)), LabelPath),
        new("list_reports", "List generated reports.",
            () => Props(("projectId", "string", false)), ReportsList),
        new("get_report", "Get one report.",
            () => Props(("reportId", "string", true), ("projectId", "string", false)), ReportGet),
        new("generate_report", "Generate a board snapshot report.",
            () => Props(("createdByType", "string", false), ("createdById", "string", false), ("projectId", "string", false)), ReportGenerate),
    ];

    private static async Task<McpToolText> LabelsList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/labels", null, ct);
    }

    private static async Task<McpToolText> LabelMatch(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var path = Text(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new McpToolText(true, "missing 'path'");
        var response = await api.SendAsync(HttpMethod.Get, $"v1/projects/{projectId:D}/labels/match?path={Uri.EscapeDataString(path)}", null, ct);
        if (response.Status == 204)
            return new McpToolText(false, "No label matches this path.");
        return Format(response);
    }

    private static async Task<McpToolText> LabelPath(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "labelId", out var labelId))
            return new McpToolText(true, "missing or invalid 'labelId'");
        var path = Text(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new McpToolText(true, "missing 'path'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/labels/{labelId:D}/paths", new JsonObject { ["path"] = path }, ct);
    }

    private static async Task<McpToolText> ReportsList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/reports", null, ct);
    }

    private static async Task<McpToolText> ReportGet(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "reportId", out var reportId))
            return new McpToolText(true, "missing or invalid 'reportId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/reports/{reportId:D}", null, ct);
    }

    private static async Task<McpToolText> ReportGenerate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject();
        Put(body, "createdByType", Text(args, "createdByType") ?? "agent");
        Put(body, "createdById", Text(args, "createdById") ?? "mcp");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/reports", body, ct);
    }

}