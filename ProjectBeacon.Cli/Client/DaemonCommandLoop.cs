namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using System.Text.Json;

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
                var claimed = await _http.GetAsync("/v1/devices/me/commands?wait=25", ct);
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
                var (ok, result, error) = await _executor.ExecuteAsync(command, ct);
                await CompleteAsync(command, ok, result, error, ct);
                _state.Publish(s => s with
                {
                    LastCommand = command.Kind.ToString(),
                    LastCommandAt = DateTimeOffset.Now,
                    LastCommandOk = ok,
                    Error = ok ? null : error
                });
                _state.Log($"{command.Kind} {(ok ? "ok" : error)}");
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
                response = await _http.PostAsJsonAsync($"/v1/commands/{command.Id}/complete", new
                {
                    success = ok,
                    resultJson = result,
                    error,
                    generation = command.Generation
                }, ct);
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
