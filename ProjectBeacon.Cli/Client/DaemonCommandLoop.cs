namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ProjectBeacon.Domain.Enums;
using Domain.Common;

internal interface IDaemonLoop
{
    Task RunAsync(CancellationToken ct);
}

internal sealed class DaemonCommandLoop : IDaemonLoop
{
    private const int MaxCompletionRetries = 4;

    private readonly HttpClient _http;
    private readonly IDaemonCommandExecutor _executor;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;
    private readonly ConcurrentDictionary<Guid, Task> _inFlightPrompts = new();

    public DaemonCommandLoop(
        DaemonDependencies deps,
        IDaemonCommandExecutor executor,
        IDaemonRuntimeState state,
        DaemonTiming timing)
    {
        _http = deps.Http;
        _executor = executor;
        _state = state;
        _timing = timing;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claimed = await _http.GetAsync(ApiRoutes.DeviceCommandsClaim, ct);
                if (claimed.StatusCode == System.Net.HttpStatusCode.NoContent)
                    continue;
                if (!claimed.IsSuccessStatusCode)
                {
                    await _timing.DelayAsync(_timing.CommandErrorDelay, ct);
                    continue;
                }

                var command = await claimed.Content.ReadFromJsonAsync<WorkstationDaemon.CommandWire>(DaemonJson.Json, ct);
                if (command is null)
                    continue;
                if (command.Kind is WorkstationCommandKind.ChatPrompt or WorkstationCommandKind.RunPipelineSession)
                    await StartPromptWithControlLaneAsync(command, ct);
                else
                    await ProcessCommandAsync(command, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (JsonException ex)
            {
                _state.Log($"malformed command wire: {ex.Message}");
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _state.Log(ex.Message);
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // A command loop may be stopped while several prompts are still active. They share
        // the daemon shutdown token and must finish their cancellation/completion paths before
        // the loop is considered stopped.
        var inFlight = _inFlightPrompts.Values.ToArray();
        if (inFlight.Length > 0)
        {
            try { await Task.WhenAll(inFlight); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task StartPromptWithControlLaneAsync(
        WorkstationDaemon.CommandWire prompt, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlightPrompts.TryAdd(prompt.Id, completion.Task))
        {
            _state.Log($"duplicate in-flight prompt command {prompt.Id}; processing in the command loop");
            await ProcessPromptWithControlLaneAsync(prompt, ct);
            return;
        }

        _ = Task.Run(() => ExecutePromptInBackgroundAsync(prompt, ct, completion), CancellationToken.None);
    }

    private async Task ExecutePromptInBackgroundAsync(
        WorkstationDaemon.CommandWire prompt,
        CancellationToken ct,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await ProcessPromptWithControlLaneAsync(prompt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The shutdown path below waits for the prompt task to observe cancellation.
        }
        catch (Exception ex)
        {
            _state.Log($"{prompt.Kind} background execution failed: {ex.Message}");
        }
        finally
        {
            completion.TrySetResult(true);
            _inFlightPrompts.TryRemove(prompt.Id, out _);
        }
    }

    /// <summary>
    /// ChatPrompt blocks while OpenCode generates. Run a narrow side poller so abort,
    /// agent switching and form replies can be delivered without waiting for the prompt.
    /// </summary>
    private async Task ProcessPromptWithControlLaneAsync(
        WorkstationDaemon.CommandWire prompt, CancellationToken ct)
    {
        using var controlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var controlTask = RunControlLaneAsync(controlCts.Token, ct);
        try
        {
            await ProcessCommandAsync(prompt, ct);
        }
        finally
        {
            controlCts.Cancel();
            try { await controlTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunControlLaneAsync(CancellationToken pollingToken, CancellationToken executionToken)
    {
        while (!pollingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(
                    ApiRoutes.DeviceCommandsControlClaim, pollingToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    continue;
                if (!response.IsSuccessStatusCode)
                {
                    await _timing.DelayAsync(_timing.CommandErrorDelay, pollingToken);
                    continue;
                }

                var command = await response.Content.ReadFromJsonAsync<WorkstationDaemon.CommandWire>(DaemonJson.Json, pollingToken);
                if (command is null)
                    continue;
                if (command.Kind is not (WorkstationCommandKind.ChatAbort
                    or WorkstationCommandKind.ChatSwitchAgent
                    or WorkstationCommandKind.ChatReplyForm))
                {
                    await ProcessCommandAsync(command, executionToken, rejectUnexpectedControlCommand: true);
                    continue;
                }
                await ProcessCommandAsync(command, executionToken);
            }
            catch (OperationCanceledException) when (pollingToken.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException ex)
            {
                _state.Log($"malformed control command wire: {ex.Message}");
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, pollingToken); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _state.Log($"control lane: {ex.Message}");
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, pollingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessCommandAsync(
        WorkstationDaemon.CommandWire command,
        CancellationToken ct,
        bool rejectUnexpectedControlCommand = false)
    {
        bool ok;
        string? result;
        string? error;
        if (rejectUnexpectedControlCommand)
            (ok, result, error) = (false, null, "Command is not allowed in the control-only queue.");
        else
            (ok, result, error) = await _executor.ExecuteAsync(command, ct);

        await CompleteAsync(command, ok, result, error, ct);
        _state.Publish(s => s with
        {
            LastCommand = command.Kind.ToString(),
            LastCommandAt = DateTimeOffset.Now,
            LastCommandOk = ok,
            Error = ok ? null : error
        });
        _state.Log($"{command.Kind} {(ok ? "ok" : error)}");
        if (ok && command.Kind == WorkstationCommandKind.UpdateClient)
            _state.RequestStop();
    }

    /// <summary>
    /// Posts the completion, retrying only transport failures and 5xx responses with bounded
    /// exponential backoff. A 4xx is final (terminal state or generation mismatch), and the
    /// command body is never re-executed. When retries are exhausted the command stays Running;
    /// the server-side recovery rule resolves it — re-queued for idempotent kinds, failed for
    /// at-most-once kinds — once the device state proves the attempt gone or the long-stale
    /// lease grace has passed.
    /// </summary>
    private async Task CompleteAsync(WorkstationDaemon.CommandWire command, bool ok, string? result, string? error, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            string? transportError = null;
            try
            {
                response = await _http.PostAsJsonAsync(ApiRoutes.CommandComplete(command.Id), new CommandCompletionRequest(ok, result, error, command.Generation), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }

            if (response is { IsSuccessStatusCode: true })
                return;
            var status = response is null ? 0 : (int)response.StatusCode;
            if (status is >= 400 and < 500)
            {
                _state.Log($"complete {command.Kind} {status}; not retrying");
                return;
            }
            if (attempt >= MaxCompletionRetries)
            {
                _state.Log($"complete {command.Kind} failed after {attempt + 1} attempts ({(status > 0 ? status.ToString() : transportError)}); giving up — server-side recovery resolves the command (re-queue or failure) after the lease grace");
                return;
            }
            _state.Log($"complete {command.Kind} {(status > 0 ? status.ToString() : transportError)}; retrying");
            await _timing.DelayAsync(delay, ct);
            delay = delay * 2;
        }
    }
}
