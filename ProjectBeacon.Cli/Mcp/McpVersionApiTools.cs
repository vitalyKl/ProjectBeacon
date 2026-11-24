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
            sourcePolicies,
            policyDto["releaseManagementEnabled"]?.GetValue<bool>() ?? false,
            policyDto["deploymentVersionEndpoint"]?.GetValue<string>());
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

        var deployment = await CheckDeploymentEndpointAsync(
            policy.DeploymentVersionEndpoint, officialVersion, ct);
        var deploymentDrift = deployment["status"]?.GetValue<string>() == "drift";
        var deploymentUnavailable = deployment["status"]?.GetValue<string>() == "unreachable";
        drift |= deploymentDrift;

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
            ["deployment"] = deployment,
            ["tagVerification"] = "not_checked_by_version_check"
        };
        if (officialVersion is null)
            result["status"] = "official_version_not_initialized";
        else if (deploymentUnavailable)
            result["status"] = "deployment_unreachable";
        else
            result["status"] = drift ? "drift" : "in_sync";

        return new McpToolText(false, result.ToJsonString(Pretty));
    }

    internal static async Task<JsonObject> CheckDeploymentEndpointAsync(
        string? endpoint,
        string? officialVersion,
        CancellationToken ct,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new JsonObject { ["status"] = "not_configured" };
        if (!ProjectVersionPolicyCatalog.TryValidateDeploymentVersionEndpoint(endpoint, out var validationError))
            return new JsonObject
            {
                ["status"] = "invalid_configuration",
                ["error"] = validationError
            };

        var ownsClient = httpClient is null;
        using var handler = ownsClient
            ? new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            }
            : null;
        var client = httpClient ?? new HttpClient(handler!, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new JsonObject { ["status"] = "unreachable", ["error"] = "Deployment version endpoint redirected; redirects are not followed." };
            if (!response.IsSuccessStatusCode)
                return new JsonObject
                {
                    ["status"] = "unreachable",
                    ["httpStatus"] = (int)response.StatusCode,
                    ["error"] = "Deployment version endpoint returned a non-success status."
                };
            if (response.Content.Headers.ContentLength is > 65536)
                return new JsonObject { ["status"] = "unreachable", ["error"] = "Deployment version response exceeds 64 KB." };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                    break;
                if (buffer.Length + read > 65536)
                    return new JsonObject { ["status"] = "unreachable", ["error"] = "Deployment version response exceeds 64 KB." };
                buffer.Write(chunk, 0, read);
            }

            JsonObject data;
            try
            {
                data = JsonNode.Parse(buffer.ToArray()) as JsonObject
                    ?? throw new JsonException("Response must be a JSON object.");
            }
            catch (JsonException exception)
            {
                return new JsonObject { ["status"] = "unreachable", ["error"] = "Deployment version response is not valid JSON: " + exception.Message };
            }

            var version = data["version"]?.GetValue<string>();
            var sha = data["gitSha"]?.GetValue<string>();
            var validSha = sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit);
            var matches = officialVersion is not null
                && string.Equals(version, officialVersion, StringComparison.Ordinal)
                && validSha;
            return new JsonObject
            {
                ["status"] = matches ? "in_sync" : "drift",
                ["deployedVersion"] = version,
                ["deployedCommitSha"] = validSha ? sha : null,
                ["officialVersion"] = officialVersion,
                ["versionMatches"] = officialVersion is not null
                    && string.Equals(version, officialVersion, StringComparison.Ordinal),
                ["gitShaValid"] = validSha,
                ["endpointHost"] = new Uri(endpoint).Host
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new JsonObject { ["status"] = "unreachable", ["error"] = exception.Message };
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
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

        var milestonesResponse = await api.SendAsync(
            HttpMethod.Get, $"v1/projects/{projectId:D}/milestones", null, ct);
        JsonArray milestones = [];
        string? milestonesError = null;
        if (milestonesResponse.Ok)
        {
            try
            {
                milestones = JsonNode.Parse(milestonesResponse.Body) as JsonArray ?? [];
            }
            catch (JsonException exception)
            {
                return new McpToolText(true, "Could not parse project milestones: " + exception.Message);
            }
        }
        else
            milestonesError = $"HTTP {milestonesResponse.Status}";

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
            ["milestones"] = milestones,
            ["milestonesError"] = milestonesError,
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
