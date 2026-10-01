using System.Diagnostics;
using ProjectBeacon.Cli.Client;
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
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private int _restartCount;
    private int _stopRequested;
    private bool _readersOpen;
    private DateTimeOffset _readySince;
    private CancellationTokenSource? _superviseCts;
    private Task? _superviseTask;
    internal TimeSpan SupervisePollDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal Task? Supervision => _superviseTask;

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

    internal void SetActualVramMb(long mb)
    {
        ActualVramMb = mb < 0 ? 0 : mb;
        VramFootprintMb = ActualVramMb > 0 ? ActualVramMb : EstimatedVramMb;
    }

    internal void ArmSupervision(LlamaSwapModelSpec spec) => StartSupervision(spec);

    public async Task StartAsync(LlamaSwapModelSpec spec, CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _stopRequested) == 1)
                return;
            await StartCoreAsync(spec, ct);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartCoreAsync(LlamaSwapModelSpec spec, CancellationToken ct)
    {
        if (Volatile.Read(ref _stopRequested) == 1)
            return;

        if (_fsm.Current == BackendState.Ready && HasLiveProcess())
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
            _readySince = DateTimeOffset.UtcNow;
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
            _readySince = DateTimeOffset.UtcNow;
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
        if (!ProcessControl.TryBeginDrain(process))
        {
            process.Dispose();
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: failed to drain output.");
            Log?.Invoke($"{Name}: start FAILED {sw.ElapsedMilliseconds}ms — output drain");
            return;
        }
        await KillProcessAsync();
        _process = process;
        _readersOpen = true;

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
                    if (Volatile.Read(ref _stopRequested) == 1)
                    {
                        await KillProcessAsync();
                        _fsm.ForceReset();
                        return;
                    }
                    _fsm.TryTransition(BackendState.Ready);
                    _readySince = DateTimeOffset.UtcNow;
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
            await KillProcessAsync();
            _fsm.TryTransition(BackendState.Faulted, $"{Name}: start cancelled.");
            throw;
        }

        await KillProcessAsync();
        _fsm.TryTransition(BackendState.Faulted, $"{Name}: did not become healthy.");
        Log?.Invoke($"{Name}: start FAILED {sw.ElapsedMilliseconds}ms — health timeout");
    }

    private bool HasLiveProcess() => _process is { HasExited: false };

    private async Task KillProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;
        await ProcessControl.KillAsync(process);
        _readersOpen = false;
        try { process.Dispose(); } catch { }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        try
        {
            var supervise = DetachSupervision();
            if (supervise is not null)
            {
                try
                {
                    await supervise.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }

            await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_fsm.Current == BackendState.Ready)
                    _fsm.TryTransition(BackendState.Stopping);
                await KillProcessAsync().ConfigureAwait(false);
                ClearFootprint();
                _restartCount = 0;
                _readySince = default;
                _fsm.ForceReset();
                Log?.Invoke(SkipRealProcess ? $"{Name}: stop (skip)" : $"{Name}: stop");
            }
            finally
            {
                _startGate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _stopRequested, 0);
        }

        ct.ThrowIfCancellationRequested();
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
        Interlocked.Exchange(ref _stopRequested, 1);
        var supervise = DetachSupervision();
        try { supervise?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            if (_readersOpen)
            {
                try { _process.CancelOutputRead(); } catch { }
                try { _process.CancelErrorRead(); } catch { }
            }
        }
        _process?.Dispose();
        _process = null;
        _http.Dispose();
        _startGate.Dispose();
    }

    private void StartSupervision(LlamaSwapModelSpec spec)
    {
        if (Volatile.Read(ref _stopRequested) == 1)
            return;
        if (_superviseTask is { IsCompleted: false })
            return;
        _superviseCts?.Dispose();
        var cts = new CancellationTokenSource();
        _superviseCts = cts;
        _superviseTask = SuperviseLoopAsync(spec, cts.Token);
    }

    private Task? DetachSupervision()
    {
        var cts = _superviseCts;
        var task = _superviseTask;
        _superviseCts = null;
        _superviseTask = null;
        try { cts?.Cancel(); } catch { }
        if (task is null)
            cts?.Dispose();
        else
            _ = task.ContinueWith(_ => cts?.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return task;
    }

    private async Task SuperviseLoopAsync(LlamaSwapModelSpec spec, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _stopRequested) == 0)
            {
                try
                {
                    await Task.Delay(SupervisePollDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _stopRequested) == 1)
                    return;

                if (HasLiveProcess())
                {
                    if (_readySince != default && DateTimeOffset.UtcNow - _readySince > Backoff[^1])
                    {
                        lock (_restartGate)
                            _restartCount = 0;
                    }
                    continue;
                }

                TimeSpan delay;
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

                await KillProcessAsync().ConfigureAwait(false);

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _stopRequested) == 1)
                    return;

                _fsm.ForceReset();
                try
                {
                    await _startGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref _stopRequested) == 1)
                            return;
                        await StartCoreAsync(spec, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _startGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
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
