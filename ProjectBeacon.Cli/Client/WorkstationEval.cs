namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Runtime;

public static class WorkstationEval
{
    public static async Task<Result<string>> RunEvalTurnAsync(
        string root,
        string payloadJson,
        ClientOpenCodeServe openCode,
        IAgentRuntime runtime,
        TimeSpan pollInterval,
        TimeSpan idleTimeout,
        TimeSpan maxDuration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("root is required.");
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Result.Failure<string>("payload is required.");

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<string>("payload must be valid JSON.");
        }

        var evalRunId = payload.TryGetProperty("evalRunId", out var runIdElement) && runIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(runIdElement.GetString(), out var runId)
                ? runId
                : Guid.Empty;
        if (evalRunId == Guid.Empty)
            return Result.Failure<string>("evalRunId is required.");

        var prompt = payload.TryGetProperty("prompt", out var promptElement) ? promptElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return Result.Failure<string>("prompt is required.");

        var relativePath = payload.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var validatedPath = WorkspacePath.ResolveInRoot(root, relativePath, relativeOnly: true);
        if (!validatedPath.Success)
            return Result.Failure<string>(validatedPath.Error ?? "path is not inside root.");

        var model = payload.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
        var title = payload.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : "Beacon eval";
        var controls = payload.TryGetProperty("controls", out var controlsElement) && controlsElement.ValueKind == JsonValueKind.Object
            ? controlsElement
            : default;
        if (controls.ValueKind == JsonValueKind.Object)
        {
            if (controls.TryGetProperty("model", out var pinnedModel) && pinnedModel.ValueKind == JsonValueKind.String)
            {
                var pinned = pinnedModel.GetString();
                if (!string.IsNullOrWhiteSpace(pinned))
                    model = pinned;
            }
            if (controls.TryGetProperty("timeoutSeconds", out var timeoutElement)
                && timeoutElement.TryGetInt32(out var timeoutSeconds)
                && timeoutSeconds > 0)
                maxDuration = TimeSpan.FromSeconds(timeoutSeconds);
            if (controls.TryGetProperty("repoRevision", out var revisionElement))
            {
                var expected = revisionElement.GetString();
                if (!string.IsNullOrWhiteSpace(expected))
                {
                    var head = GitHead(validatedPath.Value!);
                    if (!string.Equals(head, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                        return Result.Failure<string>("Repository revision does not match the eval pin.");
                    var porcelain = GitPorcelain(validatedPath.Value!);
                    if (porcelain is null)
                        return Result.Failure<string>("Repository status could not be read.");
                    if (porcelain.Length > 0)
                        return Result.Failure<string>("Working tree is dirty; eval requires a clean revision.");
                }
            }
        }

        double? temperature = null;
        string? reasoningEffort = null;
        string? toolPermissions = null;
        if (controls.ValueKind == JsonValueKind.Object)
        {
            if (controls.TryGetProperty("temperature", out var temperatureElement) && temperatureElement.TryGetDouble(out var pinnedTemperature))
                temperature = pinnedTemperature;
            if (controls.TryGetProperty("reasoningEffort", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
                reasoningEffort = reasoningElement.GetString();
            if (controls.TryGetProperty("toolPermissions", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.String)
                toolPermissions = toolsElement.GetString();
        }

        await openCode.TickAsync(validatedPath.Value, ct);
        if (!openCode.Status.Healthy)
            return Result.Failure<string>(openCode.Status.Error ?? "OpenCode is not running.");

        var sessionId = await runtime.CreateSessionAsync(string.IsNullOrWhiteSpace(title) ? "Beacon eval" : title, ct);
        await runtime.SendPromptAsync(sessionId, prompt, model, ct, new AgentPromptControls(temperature, reasoningEffort, toolPermissions));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(idleTimeout / pollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(maxDuration / pollInterval));
        var quiet = 0;
        var idle = false;
        for (var i = 0; i < maxIterations && !ct.IsCancellationRequested; i++)
        {
            var parts = new List<AgentMessagePart>();
            await foreach (var part in runtime.StreamPartsAsync(sessionId, ct))
                parts.Add(part);
            var added = 0;
            foreach (var part in parts)
            {
                if (string.Equals(part.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = part.ExternalId ?? $"{part.Role}|{part.Kind}|{part.Body}";
                if (seen.Add(key))
                    added++;
            }
            quiet = added > 0 ? 0 : quiet + 1;
            if (quiet >= maxQuiet && seen.Count > 0)
            {
                idle = true;
                break;
            }
            await Task.Delay(pollInterval, ct);
        }

        var usage = await runtime.ReadUsageAsync(sessionId, ct);
        int? exitCode = null;
        string? checkOutput = null;
        if (idle && seen.Count > 0)
        {
            var check = ReadCheckCommand(payload, controls);
            if (!string.IsNullOrWhiteSpace(check))
            {
                var checkResult = EvalCheck.Execute(validatedPath.Value!, check);
                exitCode = checkResult.ExitCode;
                checkOutput = checkResult.Output;
            }
        }
        var result = new
        {
            evalRunId,
            sessionId,
            promptTokens = usage.PromptTokens,
            completionTokens = usage.CompletionTokens,
            turnCount = Math.Max(usage.AssistantMessages, seen.Count),
            exitCode,
            checkOutput,
            interrupted = !idle,
            transcriptRef = $"opencode:session/{sessionId}"
        };
        return Result.Ok(JsonSerializer.Serialize(result));
    }

    public static Result<string> RunReviewCheck(string root, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("root is required.");
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<string>("payload must be valid JSON.");
        }

        var reviewRunId = payload.TryGetProperty("reviewRunId", out var runElement) ? runElement.GetString() : null;
        if (!Guid.TryParse(reviewRunId, out _))
            return Result.Failure<string>("reviewRunId is required.");
        var check = payload.TryGetProperty("checkCommand", out var checkElement) ? checkElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(check))
            return Result.Failure<string>("checkCommand is required.");
        var relativePath = payload.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var validatedPath = WorkspacePath.ResolveInRoot(root, relativePath, relativeOnly: true);
        if (!validatedPath.Success)
            return Result.Failure<string>(validatedPath.Error ?? "path is not inside root.");

        var checkResult = EvalCheck.Execute(validatedPath.Value!, check);
        var result = new
        {
            reviewRunId,
            exitCode = checkResult.ExitCode,
            checkOutput = checkResult.Output
        };
        return Result.Ok(JsonSerializer.Serialize(result));
    }

    private static string? ReadCheckCommand(JsonElement payload, JsonElement controls)
    {
        if (controls.ValueKind == JsonValueKind.Object
            && controls.TryGetProperty("checkCommand", out var pinned)
            && pinned.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pinned.GetString()))
            return pinned.GetString();
        return payload.TryGetProperty("checkCommand", out var checkElement) ? checkElement.GetString() : null;
    }

    private static string? GitPorcelain(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var (code, stdout, _, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (timedOut || code != 0)
                return null;
            return stdout.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GitHead(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var (code, stdout, _, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (timedOut || code != 0)
                return null;
            return stdout.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
