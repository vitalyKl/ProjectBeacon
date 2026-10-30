namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client.AgentHarness;
/// <summary>
/// Runs an eval turn or a review check inside a project root. Eval requires the pinned revision and a clean working tree. Review check requires reviewRunId and checkCommand.
/// </summary>
public static class WorkstationEval
{
    public static Task<Result<string>> RunEvalTurnAsync(
        string root,
        string payloadJson,
        IAgentRuntime runtime,
        TimeSpan pollInterval,
        TimeSpan idleTimeout,
        TimeSpan maxDuration,
        CancellationToken ct)
        => RunEvalTurnAsync(root, payloadJson, null, runtime, pollInterval, idleTimeout, maxDuration, ct, null);

    public static async Task<Result<string>> RunEvalTurnAsync(
        string root,
        string payloadJson,
        ClientOpenCodeServe? openCode,
        IAgentRuntime runtime,
        TimeSpan pollInterval,
        TimeSpan idleTimeout,
        TimeSpan maxDuration,
        CancellationToken ct,
        AgentHarnessAuthorization? harnessAuthorization = null,
        string? runtimeName = null)
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
        string? pinnedRevision = null;
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
                    pinnedRevision = expected.Trim();
                    var head = GitHead(validatedPath.Value!);
                    if (!string.Equals(head, pinnedRevision, StringComparison.OrdinalIgnoreCase))
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

        var workspaceResult = await EvalWorktree.CreateAsync(
            validatedPath.Value!,
            pinnedRevision,
            evalRunId,
            openCode,
            ct);
        if (workspaceResult.Workspace is null)
            return Result.Failure<string>(workspaceResult.Error ?? "Could not prepare an isolated eval workspace.");

        await using var evalWorkspace = workspaceResult.Workspace;
        var evalDirectory = evalWorkspace.WorkingDirectory;
        var evalAuthorization = harnessAuthorization is null
            ? null
            : harnessAuthorization with { WorkingDirectory = evalDirectory };

        if (openCode is not null)
        {
            await openCode.TickAsync(evalDirectory, ct);
            if (!openCode.Status.Healthy)
                return Result.Failure<string>(openCode.Status.Error ?? "OpenCode is not running.");
        }
        else if (runtime is IAgentRuntimeCapabilities capabilities)
        {
            var readiness = await capabilities.EnsureReadyAsync(evalDirectory, ct);
            if (!readiness.Ready)
                return Result.Failure<string>(readiness.Error ?? "The selected agent runtime is not ready.");
        }
        else
        {
            return Result.Failure<string>("The selected agent runtime does not expose readiness checks.");
        }

        var sessionId = await runtime.CreateSessionAsync(string.IsNullOrWhiteSpace(title) ? "Beacon eval" : title, ct);
        await runtime.SendPromptAsync(sessionId, prompt, model, ct, new AgentPromptControls(temperature, reasoningEffort, toolPermissions)
        {
            HarnessAuthorization = evalAuthorization
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(idleTimeout / pollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(maxDuration / pollInterval));
        var quiet = 0;
        var idle = false;
        var turnEnded = false;
        try
        {
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
                var turnStatus = await runtime.GetTurnStatusAsync(sessionId, ct);
                if (turnStatus is { Ended: true })
                {
                    turnEnded = true;
                    if (string.Equals(turnStatus.Finish, "error", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(turnStatus.Finish, "aborted", StringComparison.OrdinalIgnoreCase))
                        return Result.Failure<string>(turnStatus.Error ?? $"Eval agent turn ended with '{turnStatus.Finish}'.");
                    idle = seen.Count > 0;
                    break;
                }
    
                // A quiet interval is not completion while the runtime still owns an active turn.
                // This matters for native models that may spend time reasoning without emitting deltas.
                if (turnStatus is { Ended: false })
                    quiet = 0;
                else
                    quiet = added > 0 ? 0 : quiet + 1;
    
                if (turnStatus is null && quiet >= maxQuiet && seen.Count > 0)
                {
                    idle = true;
                    break;
                }
                await Task.Delay(pollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Do not let cancellation dispose the isolated checkout before the runtime has
            // acknowledged an abort. The cancellation is rethrown after cleanup is safe.
        }

        // A timeout or caller cancellation must stop the provider before the isolated worktree
        // is removed. Native AbortAsync waits for the active turn; OpenCode receives its interrupt
        // request. Never delete the workspace while a turn may still be using it.
        if (!turnEnded && !idle)
        {
            using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(
                CancellationToken.None,
                abortTimeout.Token);
            try
            {
                await runtime.AbortAsync(sessionId, abortCts.Token);
            }
            catch (Exception ex)
            {
                evalWorkspace.Preserve();
                return Result.Failure<string>(
                    $"Eval turn could not be stopped. Its isolated workspace was preserved at '{evalDirectory}' for safe recovery. {ex.Message}");
            }

            var stopDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 10);
            while (Stopwatch.GetTimestamp() < stopDeadline)
            {
                var status = await runtime.GetTurnStatusAsync(sessionId, CancellationToken.None);
                if (status is { Ended: true })
                {
                    turnEnded = true;
                    break;
                }
                try
                {
                    await Task.Delay(pollInterval, abortTimeout.Token);
                }
                catch (OperationCanceledException) when (abortTimeout.IsCancellationRequested)
                {
                    break;
                }
            }

            if (!turnEnded)
            {
                evalWorkspace.Preserve();
                return Result.Failure<string>(
                    $"Eval turn did not acknowledge abort. Its isolated workspace was preserved at '{evalDirectory}' for safe recovery.");
            }
        }

        ct.ThrowIfCancellationRequested();
        var usage = await runtime.ReadUsageAsync(sessionId, CancellationToken.None);
        int? exitCode = null;
        string? checkOutput = null;
        if (idle && seen.Count > 0)
        {
            var check = ReadCheckCommand(payload, controls);
            if (!string.IsNullOrWhiteSpace(check))
            {
                var checkResult = EvalCheck.Execute(evalDirectory, check);
                exitCode = checkResult.ExitCode;
                checkOutput = checkResult.Output;
            }
        }
        var selectedRuntime = runtimeName
            ?? (payload.TryGetProperty("runtime", out var runtimeElement) && runtimeElement.ValueKind == JsonValueKind.String
                ? runtimeElement.GetString()
                : null)
            ?? (runtime is NativeAgentRuntime ? "native" : "opencode");
        if (selectedRuntime is not ("native" or "opencode"))
            return Result.Failure<string>("Eval runtime must be 'native' or 'opencode'.");

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
            transcriptRef = $"{selectedRuntime}:session/{sessionId}"
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
