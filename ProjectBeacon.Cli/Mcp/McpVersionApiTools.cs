namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Mcp;
using Domain.Common;
using Domain.Entities.Projects;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> VersionTools() =>
    [
        new(McpToolNames.VersionCheck,
            "Read the project's authoritative version policy and compare the configured version-file projections in the current isolated workspace. This tool reads files itself; do not ask the model to infer or copy version values.",
            () => Props(("projectId", "string", false)), VersionCheck),
        new(McpToolNames.ReleaseContext,
            "Read the current project release/version context: authoritative version, policy, milestone list, and the active task's declared version impact. This is context only; it does not create or publish a release.",
            () => Props(("projectId", "string", false), ("taskId", "string", false)), ReleaseContext),
        new(McpToolNames.GetTaskVersionImpact,
            "Read the current version-impact declaration and its concurrency revision for the active task.",
            () => Props(("taskId", "string", false)), GetTaskVersionImpact),
        new(McpToolNames.VersionImpact,
            "Declare the active task's version impact as none, patch, minor, or major, with a concise rationale. Read get_task_version_impact first and pass its revision. This declaration does not change the official project version.",
            () => Props(("taskId", "string", false), ("impact", "string", true),
                ("rationale", "string", true), ("expectedRevision", "integer", true)), SetTaskVersionImpact),
        new(McpToolNames.ReviewVersionImpact,
            "As the active Reviewer, confirm or challenge the task's declared version impact. Read get_task_version_impact first and pass its revision plus a review note.",
            () => Props(("taskId", "string", false), ("decision", "string", true),
                ("note", "string", true), ("expectedRevision", "integer", true)), ReviewTaskVersionImpact)
    ];

    private static async Task<McpToolText> VersionCheck(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);

        var response = await api.SendAsync(
            HttpMethod.Get, $"v1/projects/{projectId:D}/version-policy", null, ct);
        if (!response.Ok)
            return Format(response);
        JsonObject policyDto;
        try
        {
            policyDto = JsonNode.Parse(response.Body) as JsonObject
                ?? throw new JsonException("Version policy response is not an object.");
        }
        catch (JsonException exception)
        {
            return new McpToolText(true, "Could not parse version policy response: " + exception.Message);
        }

        var sources = policyDto["sources"] as JsonArray ?? [];
        var sourcePolicies = new List<ProjectVersionSourcePolicy>();
        foreach (var node in sources)
        {
            if (node is not JsonObject source
                || source["path"]?.GetValue<string>() is not { } sourcePath
                || source["format"]?.GetValue<string>() is not { } format
                || source["selector"]?.GetValue<string>() is not { } selector)
                return new McpToolText(true, "Version policy contains an invalid source entry.");
            sourcePolicies.Add(new ProjectVersionSourcePolicy(sourcePath, format, selector));
        }

        var policy = new ProjectVersionPolicy(
            policyDto["scheme"]?.GetValue<string>() ?? string.Empty,
            policyDto["componentCount"]?.GetValue<int>() ?? 0,
            policyDto["calendarFormat"]?.GetValue<string>(),
            policyDto["tagPattern"]?.GetValue<string>() ?? string.Empty,
            policyDto["allowPrerelease"]?.GetValue<bool>() ?? false,
            sourcePolicies);
        if (!ProjectVersionPolicyCatalog.Validate(policy, out var validationError))
            return new McpToolText(true,
                "Stored version policy is invalid; no version-file comparison was performed: " + validationError);

        var officialVersion = policyDto["officialVersion"]?.GetValue<string>();
        var root = env.Get("BEACON_WORKSPACE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new McpToolText(true,
                "The current isolated workspace root is unavailable; version policy was read but source files were not inspected.");

        var workspace = new FileWorkspace(root);
        var checks = new JsonArray();
        var drift = false;
        foreach (var source in policy.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var read = workspace.ReadFilePage(source.Path, 0, FileWorkspace.MaxReadPageSize);
            if (!read.Success || read.Value is null)
            {
                drift = true;
                checks.Add(new JsonObject
                {
                    ["path"] = source.Path,
                    ["format"] = source.Format,
                    ["status"] = "missing_or_unsafe",
                    ["error"] = read.Error ?? "File could not be read safely."
                });
                continue;
            }
            if (read.Value.HasMore)
            {
                drift = true;
                checks.Add(new JsonObject
                {
                    ["path"] = source.Path,
                    ["format"] = source.Format,
                    ["status"] = "too_large",
                    ["error"] = $"File exceeds the {FileWorkspace.MaxReadPageSize}-character inspection limit."
                });
                continue;
            }
            if (!ProjectVersionPolicyCatalog.TryExtractVersion(
                policy, source, read.Value.Content, out var projected, out var error))
            {
                drift = true;
                checks.Add(new JsonObject
                {
                    ["path"] = source.Path,
                    ["format"] = source.Format,
                    ["status"] = "invalid",
                    ["error"] = error
                });
                continue;
            }

            var matches = officialVersion is not null
                && string.Equals(projected, officialVersion, StringComparison.Ordinal);
            if (!matches)
                drift = true;
            checks.Add(new JsonObject
            {
                ["path"] = source.Path,
                ["format"] = source.Format,
                ["status"] = officialVersion is null ? "official_version_not_initialized"
                    : matches ? "in_sync" : "drift",
                ["projectedVersion"] = projected,
                ["officialVersion"] = officialVersion
            });
        }

        var result = new JsonObject
        {
            ["projectId"] = projectId.ToString("D"),
            ["scheme"] = policy.Scheme,
            ["officialVersion"] = officialVersion,
            ["policyRevision"] = policyDto["revision"]?.DeepClone(),
            ["tagPattern"] = policy.TagPattern,
            ["tagName"] = officialVersion is null ? null : ProjectVersionPolicyCatalog.FormatTag(policy, officialVersion),
            ["sourceCount"] = policy.Sources.Count,
            ["hasProjectionDrift"] = drift,
            ["sources"] = checks,
            ["tagAndDeploymentVerification"] = "not_checked_by_version_check"
        };
        if (officialVersion is null)
            result["status"] = "official_version_not_initialized";
        else
            result["status"] = drift ? "drift" : "in_sync";

        return new McpToolText(false, result.ToJsonString(Pretty));
    }

    private static async Task<McpToolText> ReleaseContext(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);

        var policy = await api.SendAsync(
            HttpMethod.Get, $"v1/projects/{projectId:D}/version-policy", null, ct);
        if (!policy.Ok)
            return Format(policy);

        JsonObject policyJson;
        try
        {
            policyJson = JsonNode.Parse(policy.Body) as JsonObject
                ?? throw new JsonException("Version policy response is not an object.");
        }
        catch (JsonException exception)
        {
            return new McpToolText(true, "Could not parse version policy response: " + exception.Message);
        }

        var releaseManagementEnabled = policyJson["releaseManagementEnabled"]?.GetValue<bool>() == true;
        JsonArray releases = [];
        string? releasesError = null;
        if (releaseManagementEnabled)
        {
            var releaseResponse = await api.SendAsync(
                HttpMethod.Get, $"v1/projects/{projectId:D}/releases", null, ct);
            if (releaseResponse.Ok)
            {
                try
                {
                    releases = JsonNode.Parse(releaseResponse.Body) as JsonArray ?? [];
                }
                catch (JsonException exception)
                {
                    return new McpToolText(true, "Could not parse project releases: " + exception.Message);
                }
            }
            else
                releasesError = $"HTTP {releaseResponse.Status}";
        }

        var result = new JsonObject
        {
            ["projectId"] = projectId.ToString("D"),
            ["versionPolicy"] = policyJson,
            ["releaseManagementEnabled"] = releaseManagementEnabled,
            ["releases"] = releases,
            ["releasesError"] = releasesError
        };

        if (TryPipelineTask(env, args, out var taskId, out _))
        {
            var impact = await api.SendAsync(
                HttpMethod.Get, $"v1/tasks/{taskId:D}/version-impact", null, ct);
            if (impact.Ok)
                result["activeTaskVersionImpact"] = JsonNode.Parse(impact.Body);
            else
                result["activeTaskVersionImpactError"] = $"HTTP {impact.Status}";
        }

        return new McpToolText(false, result.ToJsonString(Pretty));
    }

    private static async Task<McpToolText> GetTaskVersionImpact(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var taskError))
            return new McpToolText(true, taskError);
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/version-impact", null, ct);
    }

    private static async Task<McpToolText> ReviewTaskVersionImpact(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var taskError))
            return new McpToolText(true, taskError);
        if (!string.Equals(env.Get("BEACON_PIPELINE_ROLE"), "Review", StringComparison.OrdinalIgnoreCase))
            return new McpToolText(true, "Only the active Reviewer pipeline session may review version impact.");
        var decision = Text(args, "decision");
        if (decision is not ("Confirmed" or "Challenged"))
            return new McpToolText(true, "decision must be Confirmed or Challenged.");
        var note = Text(args, "note");
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length > 1000)
            return new McpToolText(true, "note must contain 1-1000 characters.");
        var expectedRevision = IntArg(args, "expectedRevision");
        if (expectedRevision is null or < 1)
            return new McpToolText(true, "Read get_task_version_impact first and pass its positive revision.");

        return await SendAsync(api, HttpMethod.Post,
            $"v1/tasks/{taskId:D}/version-impact/review",
            new JsonObject
            {
                ["decision"] = decision,
                ["note"] = note.Trim(),
                ["expectedRevision"] = expectedRevision.Value
            }, ct);
    }

    private static async Task<McpToolText> SetTaskVersionImpact(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var taskError))
            return new McpToolText(true, taskError);
        var impact = Text(args, "impact");
        if (impact is not ("none" or "patch" or "minor" or "major"))
            return new McpToolText(true, "impact must be none, patch, minor, or major.");
        var rationale = Text(args, "rationale");
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Trim().Length > 1000)
            return new McpToolText(true, "rationale must contain 1-1000 characters.");
        var expectedRevision = IntArg(args, "expectedRevision");
        if (expectedRevision is null or < 1)
            return new McpToolText(true, "Read get_task_version_impact first and pass its positive revision.");

        return await SendAsync(api, HttpMethod.Put, $"v1/tasks/{taskId:D}/version-impact",
            new JsonObject
            {
                ["impact"] = impact,
                ["rationale"] = rationale.Trim(),
                ["expectedRevision"] = expectedRevision.Value
            }, ct);
    }
}
