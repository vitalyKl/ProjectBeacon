namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> ContextTools() =>
    [
        new("context_compile", "Compile the project brief the same way the Context page does.",
            () => Props(("taskId", "string", false), ("path", "string", false), ("repoId", "string", false),
                ("budgetTokens", "integer", false), ("includeChangedScope", "boolean", false),
                ("includeTreeCapsule", "boolean", false), ("includeHandoff", "boolean", false),
                ("projectId", "string", false)), Compile),
        new("list_context_nodes", "List context sections for the project.",
            () => Props(("projectId", "string", false)), NodesList),
        new("get_context_node", "Get one context section.",
            () => Props(("nodeId", "string", true), ("projectId", "string", false)), NodeGet),
        new("upsert_context_node", "Create or update a context section. scopeType: Project, Repo, Path, Task. source defaults to Native.",
            () => Props(("title", "string", true), ("bodyMarkdown", "string", true), ("scopeType", "string", true),
                ("sectionId", "string", false), ("key", "string", false), ("path", "string", false),
                ("source", "string", false), ("sourcePath", "string", false), ("repoId", "string", false),
                ("taskId", "string", false), ("projectId", "string", false)), NodeUpsert),
        new("delete_context_node", "Delete a context section.",
            () => Props(("nodeId", "string", true), ("projectId", "string", false)), NodeDelete),
        new("export_agents_md", "Export AGENTS.md markdown for the project.",
            () => Props(("repoId", "string", false), ("path", "string", false), ("projectId", "string", false)), ExportAgents),
    ];

    private static async Task<McpToolText> Compile(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject
        {
            ["includeHandoff"] = Flag(args, "includeHandoff") ?? false,
            ["includeChangedScope"] = Flag(args, "includeChangedScope") ?? false,
            ["includeTreeCapsule"] = Flag(args, "includeTreeCapsule") ?? false
        };
        Put(body, "path", Text(args, "path"));
        Put(body, "repoId", Text(args, "repoId"));
        var taskId = Text(args, "taskId") ?? env.Get("BEACON_TASK_ID");
        Put(body, "taskId", string.IsNullOrWhiteSpace(taskId) ? null : taskId);
        var budget = IntArg(args, "budgetTokens");
        if (budget is not null)
            body["budgetTokens"] = budget.Value;
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/context/compile", body, ct);
    }

    private static async Task<McpToolText> NodesList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/nodes", null, ct);
    }

    private static async Task<McpToolText> NodeGet(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "nodeId", out var nodeId))
            return new McpToolText(true, "missing or invalid 'nodeId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/nodes/{nodeId:D}", null, ct);
    }

    private static async Task<McpToolText> NodeUpsert(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var title = Text(args, "title");
        var bodyMarkdown = Text(args, "bodyMarkdown");
        var scopeType = Text(args, "scopeType");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(bodyMarkdown) || string.IsNullOrWhiteSpace(scopeType))
            return new McpToolText(true, "title, bodyMarkdown, and scopeType are required");
        var body = new JsonObject
        {
            ["title"] = title,
            ["bodyMarkdown"] = bodyMarkdown,
            ["scopeType"] = scopeType,
            ["source"] = Text(args, "source") ?? "Native"
        };
        Put(body, "sectionId", Text(args, "sectionId"));
        Put(body, "key", Text(args, "key"));
        Put(body, "path", Text(args, "path"));
        Put(body, "sourcePath", Text(args, "sourcePath"));
        Put(body, "repoId", Text(args, "repoId"));
        Put(body, "taskId", Text(args, "taskId"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/context/nodes", body, ct);
    }

    private static async Task<McpToolText> NodeDelete(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "nodeId", out var nodeId))
            return new McpToolText(true, "missing or invalid 'nodeId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/projects/{projectId:D}/context/nodes/{nodeId:D}", null, ct);
    }

    private static async Task<McpToolText> ExportAgents(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var query = new List<string>();
        var repoId = Text(args, "repoId");
        var path = Text(args, "path");
        if (!string.IsNullOrWhiteSpace(repoId))
            query.Add("repoId=" + Uri.EscapeDataString(repoId));
        if (!string.IsNullOrWhiteSpace(path))
            query.Add("path=" + Uri.EscapeDataString(path));
        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/export/agents-md{suffix}", null, ct);
    }

}