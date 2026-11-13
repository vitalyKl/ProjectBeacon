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
        => RunEvalTurnAsync(root, payloadJson, runtime, pollInterval, idleTimeout, maxDuration, ct, null);

    public static async Task<Result<string>> RunEvalTurnAsync(
        string root,
        string payloadJson,
        IAgentRuntime runtime,
        TimeSpan pollInterval,
        TimeSpan idleTimeout,
        TimeSpan maxDuration,
        CancellationToken ct,
        AgentHarnessAuthorization? harnessAuthorization = null)
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
            ct);
        if (workspaceResult.Workspace is null)
            return Result.Failure<string>(workspaceResult.Error ?? "Could not prepare an isolated eval workspace.");

        await using var evalWorkspace = workspaceResult.Workspace;
        var evalDirectory = evalWorkspace.WorkingDirectory;
        var evalAuthorization = harnessAuthorization is null
            ? null
            : harnessAuthorization with { WorkingDirectory = evalDirectory };

        if (runtime is not IAgentRuntimeCapabilities capabilities)
            return Result.Failure<string>("The native agent runtime does not expose readiness checks.");
        var readiness = await capabilities.EnsureReadyAsync(evalDirectory, ct);
        if (!readiness.Ready)
            return Result.Failure<string>(readiness.Error ?? "The native agent runtime is not ready.");

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
        // is removed. Native AbortAsync waits for the active turn. Never delete the workspace while a turn may still be using it.
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
        var requestedRuntime = payload.TryGetProperty("runtime", out var runtimeElement)
            && runtimeElement.ValueKind == JsonValueKind.String
                ? runtimeElement.GetString()
                : "native";
        if (!string.Equals(requestedRuntime, "native", StringComparison.Ordinal))
            return Result.Failure<string>("Only the native agent runtime is supported.");

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
            transcriptRef = $"native:session/{sessionId}"
        };
        return Result.Ok(JsonSerializer.Serialize(result));
    }

    public static async Task<Result<string>> RunReviewCheckAsync(
        string root,
        string payloadJson,
        Guid projectId,
        string? configuredWorktreeRoot,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Result.Failure<string>("Project root is unavailable.");
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<string>("payload must be valid JSON.");
        }

        var reviewRunId = ReadGuid(payload, "reviewRunId");
        var taskId = ReadGuid(payload, "taskId");
        var expectedCommandHash = ReadString(payload, "checkCommandHash");
        var checkCommand = ReadString(payload, "checkCommand");
        if (reviewRunId is null || taskId is null)
            return Result.Failure<string>("reviewRunId and taskId are required.");
        if (string.IsNullOrWhiteSpace(checkCommand) || !IsSha256(expectedCommandHash))
            return Result.Failure<string>("The trusted check command and its SHA-256 hash are required.");

        var actualCommandHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(checkCommand)))
            .ToLowerInvariant();
        if (!string.Equals(actualCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<string>("The trusted check command does not match its server-pinned hash.");

        var opened = await AgentTaskWorktree.CreateAsync(
            root, projectId, taskId.Value, configuredWorktreeRoot, ct);
        if (opened.Worktree is null)
            return Result.Failure<string>(opened.Error ?? "The persistent task branch workspace is unavailable.");

        await using var taskWorkspace = opened.Worktree;
        var checkpoint = await taskWorkspace.CommitChangesAsync(
            $"ProjectBeacon: task {taskId.Value:N} review checkpoint", ct);
        if (!checkpoint.Success || string.IsNullOrWhiteSpace(checkpoint.CommitSha))
            return Result.Failure<string>(checkpoint.Error ?? "Could not checkpoint the task branch before review.");

        var sourceSha = checkpoint.CommitSha;
        var repositoryRoot = taskWorkspace.RepositoryRoot;
        var relativeProject = Path.GetRelativePath(repositoryRoot, taskWorkspace.WorkingDirectory);
        if (Path.IsPathRooted(relativeProject)
            || relativeProject == ".."
            || relativeProject.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativeProject.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return Result.Failure<string>("The task project path escapes its repository.");

        var tree = await AgentSessionWorktree.RunGitAsync(
            taskWorkspace.WorkingDirectory, ct, [], "rev-parse", "--verify", sourceSha + "^{tree}");
        if (tree.ExitCode != 0 || tree.TimedOut || !IsGitSha(tree.StdOut.Trim()))
            return Result.Failure<string>("Could not resolve the checked task branch tree.");
        var checkedTreeSha = tree.StdOut.Trim();

        var filterConfig = await AgentSessionWorktree.RunGitAsync(
            repositoryRoot, ct, [], "config", "--name-only", "--get-regexp", "^filter\\.");
        if (filterConfig.TimedOut || filterConfig.ExitCode is not (0 or 1))
            return Result.Failure<string>("Could not inspect Git filters safely for the review worktree.");
        var disabledFilters = AgentSessionWorktree.ParseFilterDriverNames(filterConfig.StdOut);

        var parent = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "review-check-worktrees");
        Directory.CreateDirectory(parent);
        var temporaryWorktree = Path.Combine(parent, reviewRunId.Value.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            var add = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, disabledFilters,
                "worktree", "add", "--detach", temporaryWorktree, sourceSha);
            if (add.ExitCode != 0 || add.TimedOut)
                return Result.Failure<string>("Could not create a detached review worktree: " + ClipGitError(add.StdErr));

            var checkDirectory = relativeProject == "."
                ? temporaryWorktree
                : Path.GetFullPath(Path.Combine(temporaryWorktree, relativeProject));
            if (!IsWithin(temporaryWorktree, checkDirectory) || !Directory.Exists(checkDirectory))
                return Result.Failure<string>("The checked project directory is missing from its review worktree.");

            var relativePath = ReadString(payload, "path");
            var validatedPath = WorkspacePath.ResolveInRoot(checkDirectory, relativePath, relativeOnly: true);
            if (!validatedPath.Success)
                return Result.Failure<string>(validatedPath.Error ?? "path is not inside the task project.");

            var checkResult = EvalCheck.Execute(validatedPath.Value!, checkCommand);
            return Result.Ok(JsonSerializer.Serialize(new
            {
                reviewRunId,
                taskId,
                exitCode = checkResult.ExitCode,
                checkOutput = checkResult.Output,
                checkedSourceSha = sourceSha,
                checkedTreeSha,
                checkCommandHash = actualCommandHash
            }));
        }
        finally
        {
            var remove = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, CancellationToken.None, disabledFilters,
                "worktree", "remove", "--force", temporaryWorktree);
            if (Directory.Exists(temporaryWorktree))
            {
                try { Directory.Delete(temporaryWorktree, recursive: true); } catch { }
            }
            if (remove.ExitCode != 0)
                _ = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, CancellationToken.None, disabledFilters, "worktree", "prune");
        }
    }

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id)
        && id != Guid.Empty ? id : null;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ClipGitError(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= 500 ? normalized : normalized[..500];
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
