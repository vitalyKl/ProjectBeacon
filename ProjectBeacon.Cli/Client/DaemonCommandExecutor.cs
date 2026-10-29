namespace ProjectBeacon.Cli.Client;

using System.Text.Json;
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
    private readonly DaemonTiming _timing;

    public DaemonCommandExecutor(
        DaemonDependencies deps,
        IDaemonOpenCodeGate gate,
        IDaemonLlamaSync llamaSync,
        IDaemonChatSession chat,
        DaemonTiming timing)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _loadSettings = deps.LoadSettings;
        _gate = gate;
        _llamaSync = llamaSync;
        _chat = chat;
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
                            token.ExpiresAt);
                    }

                    return await _chat.PromptAsync(resolved.Value!, payload, harnessAuthorization, ct);
                }
                if (kind == WorkstationCommandKind.RunEvalTurn)
                {
                    var evalAction = await WorkstationEval.RunEvalTurnAsync(
                        command.LocalRoot,
                        payload,
                        _gate.Runtime,
                        _timing.ChatPollInterval,
                        _timing.ChatIdleTimeout,
                        _timing.ChatMaxDuration,
                        ct);
                    if (!evalAction.Success)
                        return (false, null, evalAction.Error);
                    return (true, evalAction.Value, null);
                }
                if (kind == WorkstationCommandKind.RunReviewCheck)
                {
                    var reviewAction = WorkstationEval.RunReviewCheck(command.LocalRoot, payload);
                    if (!reviewAction.Success)
                        return (false, null, reviewAction.Error);
                    return (true, reviewAction.Value, null);
                }
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
        DateTime ExpiresAt);

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
