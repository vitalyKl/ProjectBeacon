using System.Diagnostics;
using ProjectBeacon.Infrastructure.LlamaSwap;

namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Runs <c>llama-server</c> directly for one model spec (no external <c>llama-swap</c>). The
/// executable + arguments come from the spec's prebuilt <c>cmd</c>, so this is a thin
/// process wrapper plus a health check plus crash-recovery supervision.
/// </summary>
public sealed class LlamaServerBackend : IModelBackend
{
    private const int MaxRestarts = 5;
    private static readonly TimeSpan[] Backoff = new[]
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ModelBackendFsm _fsm = new();
    private readonly object _restartGate = new();
    private Process? _process;
    private int _restartCount;
    private CancellationTokenSource? _superviseCts;

    public string Name { get; init; } = "";
    public int Port { get; init; }
    internal bool SkipRealProcess { get; init; }
    internal bool FakeHealthyProcess { get; init; }
    internal int RestartCount => _restartCount;
    internal Action<string>? Log { get; set; }

    public int Endpoint => Port;
    public BackendState State => _fsm.Current;
    public string? Error => _fsm.LastError;
    public long WorkingSetMb { get; private set; }
    public long ActualVramMb { get; private set; }
    public long EstimatedVramMb { get; private set; }
    public long VramFootprintMb { get; private set; }
    internal Func<int, CancellationToken, Task<long>>? ReadProcessVramMb { get; set; }

    public async Task StartAsync(LlamaSwapModelSpec spec, CancellationToken ct)
    {
        if (_fsm.Current == BackendState.Ready && _process is { HasExited: false })
        {
            await HealthCheckAsync(ct);
            return;
        }

        if (_fsm.Current != BackendState.Idle && _fsm.Current != BackendState.Faulted)
            return;
        _fsm.TryTransition(BackendState.Starting);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (SkipRealProcess)
        {
            ApplyEstimate(spec);
            _fsm.TryTransition(BackendState.Ready);
            Log?.Invoke($"{Name}: start (skip) 0ms");
            return;
        }

        var (exe, args) = CommandLineSplit.Split(spec.LaunchCommand);
        if (string.IsNullOrWhiteSpace(exe))
        {
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: empty launch command.");
            Log?.Invoke($"{Name}: start FAILED 0ms — empty launch command");
            return;
        }
        if (FakeHealthyProcess)
        {
            ApplyEstimate(spec);
            _fsm.TryTransition(BackendState.Ready);
            _restartCount = 0;
            Log?.Invoke($"{Name}: start (fake) {sw.ElapsedMilliseconds}ms");
            return;
        }
        exe = ResolveExe(exe);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                _fsm.TryTransition(BackendState.Faulted, $"{Name}: failed to start.");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: failed to start: {ex.Message}");
            Log?.Invoke($"{Name}: start FAILED {sw.ElapsedMilliseconds}ms — {ex.Message}");
            return;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        KillQuiet();
        _process = process;

        try
        {
            for (var i = 0; i < 40 && !ct.IsCancellationRequested; i++)
            {
                if (process.HasExited)
                {
                    _fsm.TryTransition(BackendState.Faulted, $"{Name}: exited early (code {process.ExitCode}).");
                    Log?.Invoke($"{Name}: start FAILED {sw.ElapsedMilliseconds}ms — exited early (code {process.ExitCode})");
                    return;
                }
                if (await IsHealthyAsync(ct))
                {
                    _fsm.TryTransition(BackendState.Ready);
                    _restartCount = 0;
                    await ApplyMeasuredAsync(process, spec, ct);
                    StartSupervision(spec);
                    Log?.Invoke($"{Name}: start OK {sw.ElapsedMilliseconds}ms");
                    return;
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            KillQuiet();
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: start cancelled.");
            throw;
        }

        KillQuiet();
        _fsm.TryTransition(BackendState.Faulted, $"{Name}: did not become healthy.");
        Log?.Invoke($"{Name}: start FAILED {sw.ElapsedMilliseconds}ms — health timeout");
    }

    private void KillQuiet()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(2000); } catch { }
        }
        _process?.Dispose();
        _process = null;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        StopSupervision();
        if (SkipRealProcess)
        {
            _fsm.ForceReset();
            ClearFootprint();
            Log?.Invoke($"{Name}: stop (skip)");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_fsm.Current == BackendState.Ready)
            _fsm.TryTransition(BackendState.Stopping);

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(2000); } catch { }
        }
        _process?.Dispose();
        _process = null;
        ClearFootprint();
        _fsm.ForceReset();
        Log?.Invoke($"{Name}: stop {sw.ElapsedMilliseconds}ms");
        await Task.Delay(0, ct).ConfigureAwait(false);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        var healthy = await IsHealthyAsync(ct);
        if (healthy && _fsm.Current is BackendState.Starting or BackendState.Idle)
            _fsm.TryTransition(BackendState.Ready);
        else if (!healthy && _process is { HasExited: true } && _fsm.Current != BackendState.Idle)
        {
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: process exited.");
        }
        return healthy;
    }

    public void Dispose()
    {
        StopSupervision();
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process?.Dispose();
        _process = null;
        _http.Dispose();
    }

    private void StartSupervision(LlamaSwapModelSpec spec)
    {
        StopSupervision();
        var cts = new CancellationTokenSource();
        _superviseCts = cts;
        _ = SuperviseLoopAsync(spec, cts.Token);
    }

    private void StopSupervision()
    {
        _superviseCts?.Cancel();
        _superviseCts = null;
    }

    private async Task SuperviseLoopAsync(LlamaSwapModelSpec spec, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            if (_process is not { HasExited: false })
            {
                var delay = TimeSpan.Zero;
                lock (_restartGate)
                {
                    if (_restartCount >= MaxRestarts)
                    {
                        _fsm.TryTransition(BackendState.Faulted, $"{Name}: restart limit reached ({MaxRestarts}).");
                        Log?.Invoke($"{Name}: crash — restart limit ({MaxRestarts}) reached, giving up");
                        return;
                    }
                    delay = Backoff[_restartCount];
                    _restartCount++;
                    Log?.Invoke($"{Name}: crash detected, restart {_restartCount}/{MaxRestarts} (backoff {delay.TotalSeconds:0}s)");
                }

                if (_process is not null)
                {
                    try { _process.Dispose(); } catch { }
                    _process = null;
                }

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (ct.IsCancellationRequested) return;

                _fsm.ForceReset();
                _fsm.TryTransition(BackendState.Starting);
                try
                {
                    await StartAsync(spec, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    // StartAsync recorded the fault; next supervise cycle retries.
                }
            }
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        foreach (var path in new[] { "/health", "/v1/models" })
        {
            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{Port}{path}", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
        return false;
    }

    private static string ResolveExe(string exe) =>
        exe.Contains('/') || exe.Contains('\\') ? exe : WorkstationActions.Which(exe) ?? exe;

    private static long ReadWorkingSetMb(Process p)
    {
        try { return p.WorkingSet64 / (1024 * 1024); } catch { return 0; }
    }

    private void ApplyEstimate(LlamaSwapModelSpec spec)
    {
        WorkingSetMb = 0;
        ActualVramMb = 0;
        EstimatedVramMb = VramEstimate.FromSpec(spec);
        VramFootprintMb = EstimatedVramMb;
    }

    private async Task ApplyMeasuredAsync(Process process, LlamaSwapModelSpec spec, CancellationToken ct)
    {
        WorkingSetMb = ReadWorkingSetMb(process);
        EstimatedVramMb = VramEstimate.FromSpec(spec);
        ActualVramMb = 0;
        try
        {
            var read = ReadProcessVramMb ?? NvidiaSmiVramChecker.QueryProcessMbAsync;
            var actual = await read(process.Id, ct);
            if (actual > 0)
                ActualVramMb = actual;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        VramFootprintMb = ActualVramMb > 0 ? ActualVramMb : EstimatedVramMb;
    }

    private void ClearFootprint()
    {
        WorkingSetMb = 0;
        ActualVramMb = 0;
        EstimatedVramMb = 0;
        VramFootprintMb = 0;
    }
}
