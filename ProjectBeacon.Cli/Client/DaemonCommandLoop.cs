namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;

internal interface IDaemonLoop
{
    Task RunAsync(CancellationToken ct);
}

internal sealed class DaemonCommandLoop : IDaemonLoop
{
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
                await _http.PostAsJsonAsync($"/v1/commands/{command.Id}/complete", new
                {
                    success = ok,
                    resultJson = result,
                    error
                }, ct);
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
            catch (Exception ex)
            {
                _state.Log(ex.Message);
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
