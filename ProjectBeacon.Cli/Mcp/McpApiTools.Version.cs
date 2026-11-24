namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Domain.Common;
using Domain.Entities.Projects;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> VersionTools() =>
    [
        new(McpToolNames.VersionCheck,
            "Read the authoritative project version policy and compare every configured repository manifest with the Beacon official version. This is diagnostic only; it never edits files.",
            () => Props(("projectId", "string", false)), VersionCheck),
        new(McpToolNames.VersionImpact,
            "Declare version impact for the active task: none, patch, minor, or major, with a rationale. This records an agent proposal only; it does not approve or publish a release.",
            () => Props(("impact", "string", true), ("rationale", "string", true), ("taskId", "string", false)), VersionImpact),
        new(McpToolNames.GetTaskVersionImpact,
            "Read the current task version impact declaration and its optimistic concurrency revision.",
            () => Props(("taskId", "string", false)), TaskVersionImpactGet),
        new(McpToolNames.ReleaseContext,
            "Read release-related project context: authoritative version, policy, configured manifest sources, and optional active-task version impact. Release entities are not yet available in this implementation slice.",
            () => Props(("projectId", "string", false), ("taskId", "string", false)), ReleaseContext)
    ];

    private static async Task<McpToolText> VersionCheck(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);

        var response = await api.SendAsync(
            HttpMethod.Get, $"v1/projects/{projectId:D}/version-policy", null, ct);
        if (!response.Ok)
            return Fail(response.Status, response.Body);

        JsonObject? dto;
        try
        {
            dto = JsonNode.Parse(response.Body) as JsonObject;
        }
        catch (JsonException)
        {
            dto = null;
        }
        if (dto is null)
            return new McpToolText(true, "Beacon returned an invalid version policy response.");

        if (!TryPolicy(dto, out var policy, out var policyError))
            return new McpToolText(true, policyError ?? "The saved project version policy is invalid.");

        var official = dto["officialVersion"]?.GetValue<string>();
        var root = env.Get("BEACON_WORKSPACE_ROOT");
        var files = new JsonArray();
        var driftDetected = false;
        var checkedCount = 0;

        foreach (var source in policy.Sources)
        {
            var result = new JsonObject
            {
                ["path"] = source.Path,
                ["format"] = source.Format
            };
            if (string.IsNullOrWhiteSpace(root))
            {
                result["status"] = "workspace_unavailable";
            }
            else
            {
                var resolved = WorkspacePath.ResolveInsideRoot(root, source.Path);
                if (!resolved.Success || resolved.Value is null)
                {
                    result["status"] = "unsafe_path";
                    result["error"] = resolved.Error;
                    driftDetected = true;
                }
                else if (!File.Exists(resolved.Value))
                {
                    result["status"] = "missing";
                    driftDetected = true;
                }
                else
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(resolved.Value, ct);
                        if (!ProjectVersionPolicyCatalog.TryExtractVersion(
                            policy, source, content, out var manifestVersion, out var extractError))
                        {
                            result["status"] = "unreadable";
                            result["error"] = extractError;
                            driftDetected = true;
                        }
                        else
                        {
                            checkedCount++;
                            result["version"] = manifestVersion;
                            result["status"] = string.Equals(
                                manifestVersion, official, StringComparison.Ordinal)
                                ? "matches_official"
                                : "drift";
                            if (!string.Equals(manifestVersion, official, StringComparison.Ordinal))
                                driftDetected = true;
                        }
                    }
                    catch (IOException ex)
                    {
                        result["status"] = "unreadable";
                        result["error"] = ex.Message;
                        driftDetected = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        result["status"] = "unreadable";
                        result["error"] = "access denied";
                        driftDetected = true;
                    }
                }
            }
            files.Add(result);
        }

        var output = new JsonObject
        {
            ["projectId"] = projectId.ToString("D"),
            ["scheme"] = policy.Scheme,
            ["officialVersion"] = official,
            ["revision"] = dto["revision"]?.DeepClone(),
            ["sourcesConfigured"] = policy.Sources.Count,
            ["sourcesChecked"] = checkedCount,
            ["driftDetected"] = driftDetected,
            ["files"] = files,
            ["note"] = policy.Sources.Count == 0
                ? "No repository version source has been configured."
                : string.IsNullOrWhiteSpace(root)
                    ? "The API returned the authoritative policy; no local workspace root is available to inspect source files."
                    : "The check is read-only. A drift result is an advisory finding, not a release approval."
        };
        return new McpToolText(false, output.ToJsonString(Pretty));
    }

    private static async Task<McpToolText> VersionImpact(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var impact = Text(args, "impact")?.Trim();
        var rationale = Text(args, "rationale")?.Trim();
        if (impact is not ("none" or "patch" or "minor" or "major"))
            return new McpToolText(true, "impact must be one of: none, patch, minor, major.");
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 1000)
            return new McpToolText(true, "rationale is required and must be at most 1000 characters.");

        var current = await api.SendAsync(
            HttpMethod.Get, $"v1/tasks/{taskId:D}/version-impact", null, ct);
        if (!current.Ok)
            return Fail(current.Status, current.Body);

        int revision;
        try
        {
            var dto = JsonNode.Parse(current.Body) as JsonObject;
            revision = dto?["revision"]?.GetValue<int>() ?? 0;
        }
        catch (JsonException)
        {
            revision = 0;
        }
        if (revision < 1)
            return new McpToolText(true, "Beacon returned an invalid version impact revision.");

        var body = new JsonObject
        {
            ["impact"] = impact,
            ["rationale"] = rationale,
            ["expectedRevision"] = revision
        };
        return await SendAsync(
            api, HttpMethod.Put, $"v1/tasks/{taskId:D}/version-impact", body, ct);
    }

    private static async Task<McpToolText> TaskVersionImpactGet(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/version-impact", null, ct);
    }

    private static async Task<McpToolText> ReleaseContext(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);

        var policyResponse = await api.SendAsync(
            HttpMethod.Get, $"v1/projects/{projectId:D}/version-policy", null, ct);
        if (!policyResponse.Ok)
            return Fail(policyResponse.Status, policyResponse.Body);

        JsonObject? policy;
        try
        {
            policy = JsonNode.Parse(policyResponse.Body) as JsonObject;
        }
        catch (JsonException)
        {
            policy = null;
        }
        if (policy is null)
            return new McpToolText(true, "Beacon returned an invalid version policy response.");

        var taskRaw = Text(args, "taskId") ?? env.Get("BEACON_TASK_ID");
        JsonNode? impact = null;
        if (Guid.TryParse(taskRaw, out var taskId) && taskId != Guid.Empty)
        {
            var impactResponse = await api.SendAsync(
                HttpMethod.Get, $"v1/tasks/{taskId:D}/version-impact", null, ct);
            if (!impactResponse.Ok)
                return Fail(impactResponse.Status, impactResponse.Body);
            try
            {
                impact = JsonNode.Parse(impactResponse.Body);
            }
            catch (JsonException)
            {
                return new McpToolText(true, "Beacon returned an invalid task version impact response.");
            }
        }

        var result = new JsonObject
        {
            ["projectId"] = projectId.ToString("D"),
            ["officialVersion"] = policy["officialVersion"]?.DeepClone(),
            ["versionPolicy"] = policy,
            ["taskVersionImpact"] = impact,
            ["releaseManagementStage"] = "Version policy and agent declarations are available; Release entity and publication workflow are pending S2/S3.",
            ["authority"] = "Beacon project database is authoritative. Configured repository files are projections."
        };
        return new McpToolText(false, result.ToJsonString(Pretty));
    }

    private static bool TryPolicy(
        JsonObject dto, out ProjectVersionPolicy policy, out string? error)
    {
        var projection = new JsonObject
        {
            ["scheme"] = dto["scheme"]?.DeepClone(),
            ["componentCount"] = dto["componentCount"]?.DeepClone(),
            ["calendarFormat"] = dto["calendarFormat"]?.DeepClone(),
            ["tagPattern"] = dto["tagPattern"]?.DeepClone(),
            ["allowPrerelease"] = dto["allowPrerelease"]?.DeepClone(),
            ["sources"] = dto["sources"]?.DeepClone()
        };
        return ProjectVersionPolicyCatalog.TryRead(
            projection.ToJsonString(), out policy, out error);
    }
}
