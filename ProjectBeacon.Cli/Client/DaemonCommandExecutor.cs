namespace ProjectBeacon.Cli.Client;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using Application.Runtime;
using ProjectBeacon.Cli.Client.AgentHarness;
using Application.Common;
using Application.Devices;
using Domain.Enums;

internal interface IDaemonCommandExecutor
{
    Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(WorkstationDaemon.CommandWire command, CancellationToken ct);
}

internal sealed class DaemonCommandExecutor : IDaemonCommandExecutor
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly IDaemonOpenCodeGate _gate;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly IDaemonChatSession _chat;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;

    public DaemonCommandExecutor(
        DaemonDependencies deps,
        IDaemonOpenCodeGate gate,
        IDaemonLlamaSync llamaSync,
        IDaemonChatSession chat,
        IDaemonRuntimeState state,
        DaemonTiming timing)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _loadSettings = deps.LoadSettings;
        _gate = gate;
        _llamaSync = llamaSync;
        _chat = chat;
        _state = state;
        _timing = timing;
    }

    public async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(WorkstationDaemon.CommandWire command, CancellationToken ct)
    {
        try
        {
            var version = CommandEnvelope.NormalizeWireVersion(command.Version);
            if (!version.IsOk)
                return (false, null, version.Error);
            if (!CommandEnvelope.HasValidPayload(command.PayloadJson))
                return (false, null, CommandEnvelope.MalformedPayload);
            var kind = command.Kind;
            var payload = CommandEnvelope.NormalizedPayload(command.PayloadJson);
            if (kind == WorkstationCommandKind.CheckClientUpdate)
                return (true, JsonSerializer.Serialize(await ClientUpdater.CheckAsync(ct)), null);
            if (kind == WorkstationCommandKind.UpdateClient)
            {
                async Task ReportProgressAsync(ClientUpdateProgress progress, CancellationToken token)
                {
                    _state.Publish(status => status with { UpdateProgress = progress });
                    try
                    {
                        using var response = await _http.PostAsJsonAsync(
                            $"/v1/commands/{command.Id:D}/progress",
                            new
                            {
                                generation = command.Generation,
                                progressJson = JsonSerializer.Serialize(progress, DaemonJson.Camel)
                            },
                            token);
                        if (!response.IsSuccessStatusCode)
                            _state.Log($"client update progress report returned HTTP {(int)response.StatusCode}");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _state.Log($"client update progress could not be reported: {ex.Message}");
                    }
                }

                var update = await ClientUpdater.DownloadAndScheduleAsync(ReportProgressAsync, ct);
                return update.UpdateAvailable
                    ? (true, JsonSerializer.Serialize(update), null)
                    : (false, null, "The workstation client is already up to date.");
            }
            if (CommandSandbox.IsProjectKind(kind))
            {
                if (string.IsNullOrWhiteSpace(command.LocalRoot))
                    return (false, null, CommandSandbox.RuntimeRequired);
                if (kind == WorkstationCommandKind.ChatEnsureSession || kind == WorkstationCommandKind.ChatPrompt)
                {
                    var resolved = WorkspacePath.ResolveInRoot(command.LocalRoot, DaemonPayload.ReadPath(payload), relativeOnly: true);
                    if (!resolved.Success)
                        return (false, null, resolved.Error);
                    if (kind == WorkstationCommandKind.ChatEnsureSession)
                        return await _chat.EnsureAsync(resolved.Value!, payload, ct);

                    AgentHarnessAuthorization? harnessAuthorization = null;
                    if (_gate.Runtime is NativeAgentRuntime)
                    {
                        if (_http.BaseAddress is null)
                            return (false, null, "The daemon API base URL is not configured.");

                        Guid? requestedProjectId = null;
                        Guid? taskId = null;
                        Guid? subtaskId = null;
                        using (var promptDoc = JsonDocument.Parse(payload))
                        {
                            if (promptDoc.RootElement.TryGetProperty("projectId", out var rawProject)
                                && rawProject.ValueKind == JsonValueKind.String
                                && Guid.TryParse(rawProject.GetString(), out var parsedProject)
                                && parsedProject != Guid.Empty)
                                requestedProjectId = parsedProject;
                            if (promptDoc.RootElement.TryGetProperty("taskId", out var rawTask)
                                && rawTask.ValueKind == JsonValueKind.String
                                && Guid.TryParse(rawTask.GetString(), out var parsedTask)
                                && parsedTask != Guid.Empty)
                                taskId = parsedTask;
                            if (promptDoc.RootElement.TryGetProperty("subtaskId", out var rawSubtask)
                                && rawSubtask.ValueKind == JsonValueKind.String
                                && Guid.TryParse(rawSubtask.GetString(), out var parsedSubtask)
                                && parsedSubtask != Guid.Empty)
                                subtaskId = parsedSubtask;
                        }

                        using var tokenResponse = await _http.PostAsJsonAsync(
                            $"/v1/commands/{command.Id:D}/harness-token",
                            new { generation = command.Generation },
                            ct);
                        if (!tokenResponse.IsSuccessStatusCode)
                            return (false, null, $"Could not issue the short-lived harness token (HTTP {(int)tokenResponse.StatusCode}).");
                        var token = await tokenResponse.Content.ReadFromJsonAsync<HarnessTokenWire>(ct);
                        if (token is null || string.IsNullOrWhiteSpace(token.Token)
                            || token.ProjectId == Guid.Empty
                            || (requestedProjectId is { } requested && token.ProjectId != requested)
                            || token.CommandId != command.Id
                            || token.Generation != command.Generation
                            || token.ExpiresAt <= DateTime.UtcNow)
                            return (false, null, "The API returned an invalid or expired harness token.");

                        if (subtaskId is not null && taskId is null)
                            return (false, null, "A subtask-scoped prompt must include its parent task id.");

                        // The API derives project scope from the claimed database command; the client
                        // only cross-checks an optional project id carried in its payload.
                        harnessAuthorization = new AgentHarnessAuthorization(
                            _http.BaseAddress.AbsoluteUri.TrimEnd('/'),
                            token.Token,
                            token.ProjectId,
                            taskId,
                            command.Id,
                            command.Generation,
                            resolved.Value!,
                            token.ExpiresAt,
                            subtaskId,
                            null,
                            token.AllowedMcpTools, token.McpServers);
                    }

                    return await _chat.PromptAsync(resolved.Value!, payload, harnessAuthorization, ct);
                }
                if (kind == WorkstationCommandKind.RunEvalTurn)
                    return await RunEvalTurnAsync(command, payload, ct);
                if (kind == WorkstationCommandKind.RunReviewCheck)
                {
                    var reviewAction = WorkstationEval.RunReviewCheck(command.LocalRoot, payload);
                    if (!reviewAction.Success)
                        return (false, null, reviewAction.Error);
                    return (true, reviewAction.Value, null);
                }
                if (kind == WorkstationCommandKind.RunPipelineSession)
                    return await RunPipelineSessionAsync(command, payload, ct);

                var projectAction = kind switch
                {
                    WorkstationCommandKind.InitProject => WorkstationSetup.InitProject(command.LocalRoot, payload),
                    WorkstationCommandKind.ApplyOpencode => OpencodeConfig.ApplyOpencode(command.LocalRoot, payload),
                    _ => null
                };
                if (projectAction is not null)
                    return projectAction.Success ? (true, projectAction.Value, null) : (false, null, projectAction.Error);
            }
            if (kind == WorkstationCommandKind.ChatAbort)
                return await _chat.AbortAsync(payload, ct);
            if (kind == WorkstationCommandKind.ChatSwitchAgent)
                return await _chat.SwitchAgentAsync(payload, ct);
            if (kind == WorkstationCommandKind.ChatReplyForm)
                return await _chat.ReplyFormAsync(payload, ct);
            if (kind == WorkstationCommandKind.ReloadProxy)
            {
                await _llamaSync.RunLockedAsync(() => _llama.ReloadAsync(ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.UnloadProxy)
            {
                await _llamaSync.RunLockedAsync(() => _llama.UnloadAsync(ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.SwapModel)
            {
                var name = DaemonPayload.ExtractSwapName(DaemonPayload.ReadModelName(payload));
                await _llamaSync.RunLockedAsync(() => _llama.EnsureSwapModelAsync(name, ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.ConfigureOpenCode)
                return await ConfigureOpenCodeAsync(ct);
            if (kind == WorkstationCommandKind.ReconcileDesired)
                return await ReconcileDesiredAsync(payload, ct);
            var settings = _loadSettings();
            var sandboxed = kind switch
            {
                WorkstationCommandKind.ListDir => WorkstationBrowse.ListDir(DaemonPayload.BrowseRoot(settings, payload), DaemonPayload.ReadPath(payload)),
                WorkstationCommandKind.ScanGguf => WorkstationBrowse.ScanGguf(settings.ModelsRoot ?? "", DaemonPayload.ReadPath(payload)),
                _ => null
            };
            if (sandboxed is not null)
            {
                if (!sandboxed.Success)
                    return (false, null, sandboxed.Error);
                return (true, sandboxed.Value, null);
            }

            string result = kind switch
            {
                WorkstationCommandKind.Probe => WorkstationProbe.ProbeJson(_llama.StatusWire()),
                WorkstationCommandKind.SaveWorkstation => WorkstationSetup.SaveWorkstation(payload),
                WorkstationCommandKind.Install => WorkstationSetup.Install(payload),
                _ => throw new InvalidOperationException($"Unknown command {kind}.")
            };
            return (true, result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }


    private async Task<(bool Ok, string? Result, string? Error)> RunEvalTurnAsync(
        WorkstationDaemon.CommandWire command,
        string payload,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var requestedRuntime = root.TryGetProperty("runtime", out var runtimeElement)
            && runtimeElement.ValueKind == JsonValueKind.String
                ? runtimeElement.GetString()?.Trim().ToLowerInvariant()
                : _loadSettings().AgentRuntime.Trim().ToLowerInvariant();
        if (requestedRuntime is not ("opencode" or "native"))
            return (false, null, "Eval runtime must be 'opencode' or 'native'.");
        if (string.IsNullOrWhiteSpace(command.LocalRoot))
            return (false, null, CommandSandbox.RuntimeRequired);

        ClientOpenCodeServe? evalOpenCode = null;
        Dictionary<string, string>? evalProviderEnvironment = null;
        AgentHarnessAuthorization? authorization = null;
        IAgentRuntime runtime;
        var ownsRuntime = false;
        if (requestedRuntime == "native")
        {
            runtime = _gate.Runtime is NativeAgentRuntime
                ? _gate.Runtime
                : new NativeAgentRuntime(_http, _llama, _llamaSync, _loadSettings);
            ownsRuntime = !ReferenceEquals(runtime, _gate.Runtime);
        }
        else
        {
            // Eval runs use a private OpenCode server because they execute in a detached git
            // worktree. Never restart the user's interactive OpenCode server into a temporary cwd.
            evalOpenCode = new ClientOpenCodeServe();
            runtime = new OpenCodeAgentRuntime(evalOpenCode);
            ownsRuntime = true;
        }

        try
        {
            if (requestedRuntime == "opencode")
            {
                var response = await _http.GetAsync("/v1/devices/me/opencode-connections", ct);
                if (!response.IsSuccessStatusCode)
                    return (false, null, $"opencode connections {(int)response.StatusCode}");
                var body = await response.Content.ReadAsStringAsync(ct);
                var applied = OpencodeConfig.ApplyOpenCodeConnections(body);
                evalProviderEnvironment = new Dictionary<string, string>(applied.Environment, StringComparer.Ordinal);
            }

            if (_http.BaseAddress is null)
                return (false, null, "The daemon API base URL is not configured.");

            var taskId = ReadGuid(root, "taskId");
            var evalRunId = ReadGuid(root, "evalRunId");
            if (taskId == Guid.Empty || evalRunId == Guid.Empty)
                return (false, null, "Eval requires taskId and evalRunId.");

            using var tokenResponse = await _http.PostAsJsonAsync(
                $"/v1/commands/{command.Id:D}/harness-token",
                new { generation = command.Generation },
                ct);
            if (!tokenResponse.IsSuccessStatusCode)
                return (false, null, $"Could not issue the short-lived eval harness token (HTTP {(int)tokenResponse.StatusCode}).");
            var token = await tokenResponse.Content.ReadFromJsonAsync<HarnessTokenWire>(ct);
            if (token is null
                || string.IsNullOrWhiteSpace(token.Token)
                || token.ProjectId == Guid.Empty
                || token.CommandId != command.Id
                || token.Generation != command.Generation
                || token.ExpiresAt <= DateTime.UtcNow)
                return (false, null, "The API returned an invalid or expired eval harness token.");

            authorization = new AgentHarnessAuthorization(
                _http.BaseAddress.AbsoluteUri.TrimEnd('/'),
                token.Token,
                token.ProjectId,
                taskId,
                command.Id,
                command.Generation,
                Path.GetFullPath(command.LocalRoot),
                token.ExpiresAt,
                null,
                "Eval",
                token.AllowedMcpTools, token.McpServers);

            if (requestedRuntime == "opencode")
            {
                var evalEnvironment = evalProviderEnvironment
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                evalEnvironment["BEACON_API_URL"] = _http.BaseAddress.AbsoluteUri.TrimEnd('/');
                evalEnvironment["BEACON_API_TOKEN"] = token.Token;
                evalEnvironment["BEACON_PROJECT_ID"] = token.ProjectId.ToString("D");
                evalEnvironment["BEACON_TASK_ID"] = taskId.ToString("D");
                evalEnvironment["BEACON_PIPELINE_ROLE"] = "Eval";
                evalEnvironment.Remove("BEACON_SUBTASK_ID");
                evalEnvironment.Remove("BEACON_PIPELINE_SESSION_ID");
                evalOpenCode!.SetEnvironment(evalEnvironment);
            }

            var evalAction = await WorkstationEval.RunEvalTurnAsync(
                command.LocalRoot,
                payload,
                evalOpenCode,
                runtime,
                _timing.ChatPollInterval,
                _timing.ChatIdleTimeout,
                _timing.ChatMaxDuration,
                ct,
                authorization,
                requestedRuntime);
            if (!evalAction.Success)
                return (false, null, evalAction.Error);
            return (true, evalAction.Value, null);
        }
        finally
        {
            if (ownsRuntime)
                await runtime.DisposeAsync();
            if (evalOpenCode is not null)
                await evalOpenCode.DisposeAsync();
        }
    }


    private async Task<(bool Ok, string? Result, string? Error)> RunPipelineSessionAsync(
        WorkstationDaemon.CommandWire command,
        string payload,
        CancellationToken ct)
    {
        if (_gate.Runtime is not NativeAgentRuntime runtime)
            return (false, null, "Pipeline sessions require AgentRuntime=native.");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var projectId = ReadGuid(root, "projectId");
        var taskId = ReadGuid(root, "taskId");
        var pipelineRole = root.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
            ? roleValue.GetString()?.Trim().ToLowerInvariant() switch
            {
                "planner" => "Planner",
                "actor" => "Actor",
                "review" => "Review",
                _ => null
            }
            : null;
        var rawSubtaskId = ReadGuid(root, "subtaskId");
        Guid? subtaskId = rawSubtaskId == Guid.Empty ? null : rawSubtaskId;
        var pipelineSessionId = ReadGuid(root, "sessionId");
        var prompt = root.TryGetProperty("prompt", out var promptValue) ? promptValue.GetString() : null;
        var model = root.TryGetProperty("model", out var modelValue) ? modelValue.GetString() : null;
        var title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : "ProjectBeacon pipeline";
        var reasoningEffort = root.TryGetProperty("reasoningEffort", out var effortValue) ? effortValue.GetString() : null;
        var contextLimit = root.TryGetProperty("contextLimit", out var limitValue)
            && limitValue.ValueKind == JsonValueKind.Number
            && limitValue.TryGetInt32(out var parsedLimit)
            && parsedLimit > 0
                ? parsedLimit
                : (int?)null;

        if (projectId == Guid.Empty || taskId == Guid.Empty || pipelineSessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(model))
            return (false, null, "Pipeline command requires projectId, taskId, sessionId, model, and prompt.");
        if (pipelineRole is null)
            return (false, null, "Pipeline command requires role Planner, Actor, or Review.");
        if (string.Equals(pipelineRole, "Actor", StringComparison.Ordinal) && subtaskId is null)
            return (false, null, "Actor pipeline sessions require subtaskId.");
        if (!string.Equals(pipelineRole, "Actor", StringComparison.Ordinal) && subtaskId is not null)
            return (false, null, "Only Actor pipeline sessions may include subtaskId.");
        if (subtaskId is not null && taskId == Guid.Empty)
            return (false, null, "A subtask-scoped pipeline session must include its parent task id.");
        if (_http.BaseAddress is null)
            return (false, null, "The daemon API base URL is not configured.");
        if (string.IsNullOrWhiteSpace(command.LocalRoot))
            return (false, null, CommandSandbox.RuntimeRequired);

        var readiness = await runtime.EnsureReadyAsync(command.LocalRoot, ct);
        if (!readiness.Ready)
            return (false, null, readiness.Error ?? "The native agent runtime is not ready.");

        var originalProjectRoot = readiness.WorkingDirectory ?? Path.GetFullPath(command.LocalRoot);
        var createdWorktree = await AgentSessionWorktree.CreateAsync(originalProjectRoot, pipelineSessionId, ct);
        if (createdWorktree.Workspace is null)
            return (false, null, createdWorktree.Error ?? "Could not create an isolated agent worktree.");
        await using var sessionWorktree = createdWorktree.Workspace;

        using var tokenResponse = await _http.PostAsJsonAsync(
            $"/v1/commands/{command.Id:D}/harness-token",
            new { generation = command.Generation },
            ct);
        if (!tokenResponse.IsSuccessStatusCode)
            return (false, null, $"Could not issue the short-lived harness token (HTTP {(int)tokenResponse.StatusCode}).");
        var token = await tokenResponse.Content.ReadFromJsonAsync<HarnessTokenWire>(ct);
        if (token is null || string.IsNullOrWhiteSpace(token.Token)
            || token.ProjectId != projectId
            || token.CommandId != command.Id
            || token.Generation != command.Generation
            || token.ExpiresAt <= DateTime.UtcNow)
            return (false, null, "The API returned an invalid or expired harness token.");

        var authorization = new AgentHarnessAuthorization(
            _http.BaseAddress.AbsoluteUri.TrimEnd('/'),
            token.Token,
            token.ProjectId,
            taskId,
            command.Id,
            command.Generation,
            sessionWorktree.WorkingDirectory,
            token.ExpiresAt,
            subtaskId,
            pipelineRole,
            token.AllowedMcpTools, token.McpServers,
            sessionWorktree.ProjectRoot,
            IsolatedWorkspace: true);
        var externalSessionId = await runtime.CreateSessionAsync(title ?? "ProjectBeacon pipeline", ct);
        var workstationSettings = _loadSettings();
        var pipelineMaxMinutes = Math.Clamp(workstationSettings.PipelineTurnTimeoutMinutes, 15, 60);
        var pipelineIdleMinutes = Math.Clamp(workstationSettings.PipelineIdleTimeoutMinutes, 1, Math.Min(30, pipelineMaxMinutes - 1));
        var pipelineIdleTimeout = TimeSpan.FromMinutes(pipelineIdleMinutes);
        var lastProgressAt = DateTime.UtcNow;
        string? lastProgressSignature = null;
        string? timeoutReason = null;
        var awaitingUser = false;

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TimeSpan.FromMinutes(pipelineMaxMinutes));

        async Task<bool> PublishActivityAsync(string state, AgentTurnStatus? turnStatus, CancellationToken tokenCt)
        {
            var collected = new List<AgentMessagePart>();
            await foreach (var part in runtime.StreamPartsAsync(externalSessionId, tokenCt))
            {
                // Publish the same reasoning, tool and text events used by the chat transcript.
                // Clip each entry and keep the snapshot bounded to stay below the progress API limit.
                if (string.Equals(part.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                collected.Add(part with
                {
                    Body = ClipActivityText(part.Body, 2000),
                    ToolInput = ClipActivityText(part.ToolInput, 1000),
                    ToolResult = ClipActivityText(part.ToolResult, 1500)
                });
            }
            var parts = collected.TakeLast(20).Select(part => new
            {
                role = part.Role,
                kind = part.Kind,
                body = part.Body,
                externalId = part.ExternalId,
                tool = part.Tool,
                toolStatus = part.ToolStatus,
                toolInput = part.ToolInput,
                toolResult = part.ToolResult,
                durationMs = part.DurationMs,
                complete = part.Complete
            }).ToArray();

            IReadOnlyList<AgentRuntimeForm> forms = _gate.Runtime is IAgentRuntimeCapabilities capabilities && capabilities.SupportsForms
                ? await capabilities.ListPendingFormsAsync(externalSessionId, tokenCt)
                : Array.Empty<AgentRuntimeForm>();
            var formItems = forms.Select(form =>
            {
                JsonNode? fields = null;
                try { fields = JsonNode.Parse(form.FieldsJson); } catch (JsonException) { }
                return new
                {
                    id = form.Id,
                    title = form.Title,
                    metadata = form.Metadata,
                    fields = fields ?? new JsonArray()
                };
            }).ToArray();

            // A human question is a durable pause, not an idle agent. Suspend both idle and
            // absolute turn timeouts while the form remains pending; resume the cap after reply.
            if (forms.Count > 0)
            {
                lastProgressAt = DateTime.UtcNow;
                if (!awaitingUser)
                {
                    awaitingUser = true;
                    turnCts.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }
            else if (awaitingUser)
            {
                awaitingUser = false;
                lastProgressAt = DateTime.UtcNow;
                turnCts.CancelAfter(TimeSpan.FromMinutes(pipelineMaxMinutes));
            }

            // Ignore elapsed-duration counters: only transcript/tool/form changes count
            // as progress, so a stuck tool cannot keep the turn alive indefinitely.
            var progressSignature = JsonSerializer.Serialize(new
            {
                parts = collected.Select(part => new
                {
                    part.Role,
                    part.Kind,
                    BodyLength = part.Body?.Length,
                    BodyTail = part.Body is { Length: > 256 } body ? body[^256..] : part.Body,
                    part.ExternalId,
                    part.Tool,
                    part.ToolStatus,
                    ToolInputLength = part.ToolInput?.Length,
                    ToolInputTail = part.ToolInput is { Length: > 128 } input ? input[^128..] : part.ToolInput,
                    ToolResultLength = part.ToolResult?.Length,
                    ToolResultTail = part.ToolResult is { Length: > 128 } result ? result[^128..] : part.ToolResult,
                    part.Complete
                }),
                forms = formItems
            });
            var madeProgress = !string.Equals(progressSignature, lastProgressSignature, StringComparison.Ordinal);
            if (madeProgress)
            {
                lastProgressSignature = progressSignature;
                lastProgressAt = DateTime.UtcNow;
            }

            var latest = collected.LastOrDefault();
            var activity = forms.Count > 0
                ? "Waiting for your answer"
                : latest?.Kind == "reasoning" && !string.IsNullOrWhiteSpace(latest.Body)
                    ? $"Thinking: {ClipActivityText(latest.Body, 280)}"
                    : latest?.Kind == "tool"
                        ? $"Using {latest.Tool ?? "a tool"}"
                        : latest?.Kind == "text" && !string.IsNullOrWhiteSpace(latest.Body)
                            ? ClipActivityText(latest.Body, 280)
                            : state == "completed" ? "Stage completed"
                            : state == "failed" ? "Stage failed"
                            : "Agent is working";

            var snapshot = JsonSerializer.Serialize(new
            {
                sessionId = pipelineSessionId,
                externalSessionId,
                state = forms.Count > 0 ? "waiting-for-user" : state,
                activity,
                finish = turnStatus?.Finish,
                error = turnStatus?.Error,
                updatedAt = DateTimeOffset.UtcNow,
                parts,
                forms = formItems
            }, DaemonJson.Camel);

            using var response = await _http.PostAsJsonAsync(
                $"/v1/commands/{command.Id:D}/progress",
                new { generation = command.Generation, progressJson = snapshot },
                tokenCt);
            if (!response.IsSuccessStatusCode)
            {
                _state.Log($"pipeline activity report returned HTTP {(int)response.StatusCode}");
                if (response.StatusCode is System.Net.HttpStatusCode.Conflict
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
                {
                    timeoutReason = $"The server no longer accepts progress for this pipeline command (HTTP {(int)response.StatusCode}); its session was cancelled or is no longer authorized.";
                    turnCts.Cancel();
                    tokenCt.ThrowIfCancellationRequested();
                }
            }
            return madeProgress;
        }

        try
        {
            await runtime.SendPromptAsync(
                externalSessionId,
                prompt,
                model,
                turnCts.Token,
                new AgentPromptControls(null, reasoningEffort, null)
                {
                    ContextLimit = contextLimit,
                    HarnessAuthorization = authorization
                });

            AgentTurnStatus? status;
            do
            {
                await PublishActivityAsync("running", null, turnCts.Token);
                if (!awaitingUser && DateTime.UtcNow - lastProgressAt >= pipelineIdleTimeout)
                {
                    timeoutReason = $"The pipeline agent turn made no progress for {pipelineIdleMinutes} minutes.";
                    turnCts.Cancel();
                    turnCts.Token.ThrowIfCancellationRequested();
                }

                await Task.Delay(_timing.ChatPollInterval, turnCts.Token);
                status = await runtime.GetTurnStatusAsync(externalSessionId, turnCts.Token);
            }
            while (status is null || !status.Ended);

            var usage = await runtime.ReadUsageAsync(externalSessionId, ct);
            var failed = !string.IsNullOrWhiteSpace(status.Error)
                || string.Equals(status.Finish, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.Finish, "aborted", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(status.Finish);

            await PublishActivityAsync(failed ? "failed" : "completed", status, ct);
            if (failed)
            {
                var failure = status.Error
                    ?? (string.Equals(status.Finish, "aborted", StringComparison.OrdinalIgnoreCase)
                        ? "The pipeline agent turn was aborted."
                        : "The pipeline agent turn failed.");
                sessionWorktree.Preserve();
                return (false, null, failure + $" Agent worktree preserved at {sessionWorktree.RecoveryPath}.");
            }

            var applied = await sessionWorktree.ApplyChangesAsync(ct);
            if (!applied.Success)
            {
                sessionWorktree.Preserve();
                return (false, null, (applied.Error ?? "Could not apply isolated agent changes.")
                    + $" Agent worktree preserved at {sessionWorktree.RecoveryPath}.");
            }

            return (true, JsonSerializer.Serialize(new
            {
                pipelineSessionId,
                externalSessionId,
                finish = status.Finish,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.AssistantMessages
            }), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { await runtime.AbortAsync(externalSessionId, CancellationToken.None); } catch { }
            try { await PublishActivityAsync("failed", null, CancellationToken.None); } catch { }
            sessionWorktree.Preserve();
            return (false, null, (timeoutReason
                ?? $"The pipeline agent turn exceeded the {pipelineMaxMinutes}-minute workstation limit.")
                + $" Agent worktree preserved at {sessionWorktree.RecoveryPath}.");
        }
        catch (OperationCanceledException)
        {
            sessionWorktree.Preserve();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { await runtime.AbortAsync(externalSessionId, CancellationToken.None); } catch { }
            try { await PublishActivityAsync("failed", null, CancellationToken.None); } catch { }
            sessionWorktree.Preserve();
            return (false, null, $"Pipeline agent failed ({ex.GetType().Name}). Agent worktree preserved at {sessionWorktree.RecoveryPath}.");
        }
        finally
        {
            try { await runtime.StopServicesAsync(externalSessionId, CancellationToken.None); } catch { }
        }
    }

    private static string? ClipActivityText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static Guid ReadGuid(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : Guid.Empty;

    private async Task<(bool Ok, string? Result, string? Error)> ReconcileDesiredAsync(string payload, CancellationToken ct)
    {
        await _llamaSync.SyncAsync(_loadSettings(), ct);
        var configured = await ConfigureOpenCodeAsync(ct);
        if (!configured.Ok)
            return configured;
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        if (doc.RootElement.TryGetProperty("workstation", out var workstation)
            && workstation.ValueKind == JsonValueKind.Object)
            WorkstationSetup.SaveWorkstation(workstation.GetRawText());
        var revision = doc.RootElement.TryGetProperty("revision", out var rev) && rev.TryGetInt64(out var value)
            ? value
            : 0;
        return (true, JsonSerializer.Serialize(new { revision }), null);
    }

    private sealed record HarnessTokenWire(
        string Token,
        Guid ProjectId,
        Guid CommandId,
        int Generation,
        DateTime ExpiresAt,
        IReadOnlyList<string>? AllowedMcpTools = null,
        IReadOnlyList<McpServerConfiguration>? McpServers = null);

    private async Task<(bool Ok, string? Result, string? Error)> ConfigureOpenCodeAsync(CancellationToken ct)
    {
        if (string.Equals(_loadSettings().AgentRuntime, "native", StringComparison.Ordinal))
            return (true, JsonSerializer.Serialize(new { runtime = "native", restarted = false }), null);

        var response = await _http.GetAsync("/v1/devices/me/opencode-connections", ct);
        if (!response.IsSuccessStatusCode)
            return (false, null, $"opencode connections {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync(ct);
        var applied = OpencodeConfig.ApplyOpenCodeConnections(body);
        _gate.OpenCode.SetEnvironment(applied.Environment);
        await _gate.RunExclusiveAsync(() => _gate.OpenCode.RestartAsync(_loadSettings().ProjectsRoot, ct), ct);
        return (true, JsonSerializer.Serialize(new { config = applied.ConfigPath, restarted = true, providers = applied.Environment.Count }), null);
    }
}
