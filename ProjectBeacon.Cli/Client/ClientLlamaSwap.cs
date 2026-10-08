namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.LlamaSwap;
using ModelSwapping;

public sealed class ClientLlamaSwap : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _process;
    private string _lastHash = "";
    private string? _lastBin;
    private int _port = 8080;
    private DateTime? _lastSwap;
    private string? _lastLoaded;
    private bool _runningFake;

    public LlamaSwapStatusDto Status { get; private set; } =
        new(false, false, null, null, null, "llama-swap is not started on this device.");

    public int Port => _port;

    internal int StartCount { get; private set; }
    internal bool SkipRealProcess { get; set; }
    internal string ConfigFile { get; set; } = ConfigPath;
    internal bool UseOwnSwapper { get; set; }
    internal int ConcurrentPortBase { get; set; } = 9000;
    private readonly Dictionary<string, LlamaServerBackend> _own = new();
    private readonly SwapGroupCoordinator _coordinator = new();
    internal IVramChecker VramChecker { get; set; } = new NoopVramChecker();
    internal LlamaServerBackend? OwnBackend(string name) => _own.TryGetValue(name, out var backend) ? backend : null;
    internal Action<string>? Log { get; set; }
    private string? _ownLastYaml;
    private string? _desiredSwapModel;
    private int _externalCrashes;
    private const int MaxExternalCrashes = 5;

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectBeacon", "llama-swap", "config.yaml");

    internal static bool ShouldRestart(
        string hash, string lastHash, int port, int lastPort, string bin, string? lastBin, bool running) =>
        !running
        || !string.Equals(hash, lastHash, StringComparison.Ordinal)
        || port != lastPort
        || !string.Equals(bin, lastBin, StringComparison.OrdinalIgnoreCase);

    public async Task TickAsync(string yaml, int port, string? binPath, CancellationToken ct)
    {
        if (UseOwnSwapper)
        {
            _ownLastYaml = yaml;
            await TickOwnAsync(yaml, port, ct);
            return;
        }

        var nextPort = port <= 0 ? 8080 : port;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
        if (hash != _lastHash)
        {
            var dir = Path.GetDirectoryName(ConfigFile)!;
            Directory.CreateDirectory(dir);
            var tmp = ConfigFile + ".tmp";
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, ConfigFile, overwrite: true);
        }

        var bin = binPath;
        if (string.IsNullOrWhiteSpace(bin))
            bin = WorkstationProbe.Which("llama-swap") ?? WorkstationProbe.Which("llama-swap.exe");
        if (string.IsNullOrWhiteSpace(bin))
        {
            await KillProcessAsync();
            _lastHash = hash;
            _port = nextPort;
            _lastBin = null;
            _externalCrashes = 0;
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap binary not found on this device.");
            return;
        }

        if (ShouldRestart(hash, _lastHash, nextPort, _port, bin, _lastBin, IsRunning))
        {
            var crashed = _lastBin is not null
                && !IsRunning
                && string.Equals(hash, _lastHash, StringComparison.Ordinal)
                && nextPort == _port
                && string.Equals(bin, _lastBin, StringComparison.OrdinalIgnoreCase);
            if (crashed)
            {
                if (_externalCrashes >= MaxExternalCrashes)
                {
                    Status = new LlamaSwapStatusDto(false, false, null, null, _lastSwap, $"llama-swap restart limit ({MaxExternalCrashes}).");
                    return;
                }
                _externalCrashes++;
            }
            else
                _externalCrashes = 0;
            await KillProcessAsync();
        }

        _lastHash = hash;
        _port = nextPort;
        _lastBin = bin;
        EnsureProcess(bin);
        await PollAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        if (UseOwnSwapper)
        {
            await StopOwnBackendsAsync(ct);
            _own.Clear();
            if (_ownLastYaml is null)
            {
                Status = new LlamaSwapStatusDto(false, false, null, null, null, null);
                return;
            }
            await TickOwnAsync(_ownLastYaml, _port, ct);
            return;
        }

        _externalCrashes = 0;
        await KillProcessAsync();
        if (string.IsNullOrWhiteSpace(_lastBin))
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap binary not found on this device.");
            return;
        }
        EnsureProcess(_lastBin);
        await PollAsync(ct);
    }

    public async Task UnloadAsync(CancellationToken ct)
    {
        if (UseOwnSwapper)
        {
            await StopOwnBackendsAsync(ct);
            _own.Clear();
            _lastLoaded = null;
            Status = new LlamaSwapStatusDto(false, false, null, null, null, null);
            return;
        }

        try
        {
            using var response = await _http.PostAsync($"http://127.0.0.1:{_port}/api/models/unload", null, ct);
            await PollAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"unload {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public object StatusWire() => new
    {
        available = Status.Available,
        healthy = Status.Healthy,
        loadedModel = Status.LoadedModel,
        loadedModels = Status.LoadedModels?.Select(m => new { name = m.Name, state = m.State }).ToList(),
        memory = Status.Memory,
        lastSwap = Status.LastSwap,
        error = Status.Error
    };

    private bool IsRunning => SkipRealProcess ? _runningFake : _process is { HasExited: false };

    private void EnsureProcess(string bin)
    {
        if (IsRunning)
            return;
        StartCount++;
        if (SkipRealProcess)
        {
            _runningFake = true;
            Status = new LlamaSwapStatusDto(true, true, null, null, null, null);
            return;
        }
        var psi = new ProcessStartInfo
        {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(ConfigFile);
        psi.ArgumentList.Add("-listen");
        psi.ArgumentList.Add($"127.0.0.1:{_port}");
        var launched = ProcessRunner.TryStart(psi);
        if (launched.Error == ProcessLaunchError.NotStarted)
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap failed to start.");
            return;
        }
        if (launched.Error == ProcessLaunchError.StartThrew)
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, $"llama-swap failed to start: {launched.ExceptionMessage}");
            return;
        }
        if (launched.Error == ProcessLaunchError.Drain)
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap failed to drain output.");
            return;
        }
        _process = launched.Process;
    }

    private async Task KillProcessAsync()
    {
        if (SkipRealProcess)
        {
            _runningFake = false;
            return;
        }
        var process = _process;
        _process = null;
        if (process is null)
            return;
        await ProcessControl.KillAsync(process);
        try { process.Dispose(); } catch { }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (SkipRealProcess)
        {
            Status = new LlamaSwapStatusDto(true, true, null, null, null, null);
            return;
        }
        try
        {
            using var health = await _http.GetAsync($"http://127.0.0.1:{_port}/health", ct);
            if (!health.IsSuccessStatusCode)
            {
                Status = new LlamaSwapStatusDto(true, false, null, null, _lastSwap, $"/health {(int)health.StatusCode}");
                return;
            }
            var loaded = await FetchLoadedAsync(ct);
            var joined = loaded.Count > 0 ? string.Join(", ", loaded.Select(m => m.Name)) : null;
            if (joined is not null && joined != _lastLoaded)
                _lastSwap = DateTime.UtcNow;
            _lastLoaded = joined;
            _externalCrashes = 0;
            Status = new LlamaSwapStatusDto(true, true, joined, await FetchMemoryAsync(ct), _lastSwap, null, loaded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new LlamaSwapStatusDto(true, false, null, null, _lastSwap, $"llama-swap not reachable: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<LoadedModelStatus>> FetchLoadedAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://127.0.0.1:{_port}/running", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("running", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var names = new List<LoadedModelStatus>();
            foreach (var element in arr.EnumerateArray())
            {
                var state = element.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state is not ("ready" or "starting" or "loaded" or "loading"))
                    continue;
                var model = element.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(model))
                    names.Add(new LoadedModelStatus(model!, state!));
            }
            return names;
        }
        catch
        {
            return [];
        }
    }

    private async Task<string?> FetchMemoryAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"http://127.0.0.1:{_port}/metrics", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                var idx = trimmed.LastIndexOf(' ');
                if (idx <= 0)
                    continue;
                var namePart = trimmed[..idx];
                var brace = namePart.IndexOf('{');
                var name = (brace < 0 ? namePart : namePart[..brace]).Trim();
                if (!Regex.IsMatch(name, "(?i)memory|vram|rss|resident"))
                    continue;
                return HostLoadSampler.FormatMemory(trimmed[(idx + 1)..].Trim());
            }
        }
        catch
        {
        }
        return null;
    }

    private async Task<bool> TryAdmitAsync(long need, CancellationToken ct)
    {
        if (need <= 0)
            return true;
        VramReading reading;
        try
        {
            reading = await VramChecker.ReadFreeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (reading.QueryFailed)
            return false;
        if (reading.FreeMb < 0)
            return true;
        return reading.FreeMb >= need;
    }

    private static long Footprint(LlamaServerBackend backend, LlamaSwapModelSpec spec)
    {
        if (backend.ActualVramMb > 0)
            return backend.ActualVramMb;
        if (backend.EstimatedVramMb > 0)
            return backend.EstimatedVramMb;
        return VramEstimate.FromSpec(spec);
    }

    private long ReservedExcept(string name)
    {
        long sum = 0;
        foreach (var backend in _own.Values)
        {
            if (backend.Name == name)
                continue;
            if (backend.State is not (BackendState.Ready or BackendState.Starting))
                continue;
            sum += backend.ActualVramMb > 0 ? backend.ActualVramMb : backend.EstimatedVramMb;
        }

        return sum;
    }

    private async Task StopOwnBackendsAsync(CancellationToken ct)
    {
        foreach (var backend in _own.Values)
        {
            await backend.StopAsync(ct);
            backend.Dispose();
            _coordinator.Deactivate(backend.Name);
        }
        _coordinator.UnregisterAll();
    }

    /// <summary>Remembers which swap-group model to keep resident; survives heartbeat ticks.</summary>
    internal void PreferSwapModel(string? name) => _desiredSwapModel = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    internal Task EnsureSwapModelAsync(string? name, CancellationToken ct)
    {
        PreferSwapModel(name);
        if (UseOwnSwapper && _ownLastYaml is not null)
            return TickOwnAsync(_ownLastYaml, _port, ct);
        return Task.CompletedTask;
    }

    private async Task TickOwnAsync(string yaml, int port, CancellationToken ct)
    {
        if (port > 0)
            _port = port;

        IReadOnlyList<LlamaSwapModelSpec> specs;
        try
        {
            specs = LlamaSwapConfigParser.Parse(yaml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, $"config parse failed: {ex.Message}");
            return;
        }

        var swapSpec = specs.FirstOrDefault(s => !s.Concurrent && string.Equals(s.Name, _desiredSwapModel, StringComparison.OrdinalIgnoreCase))
            ?? specs.FirstOrDefault(s => !s.Concurrent);

        var desired = new Dictionary<string, (LlamaSwapModelSpec Spec, int Port)>(StringComparer.Ordinal);
        var residentIndex = 0;
        foreach (var spec in specs)
        {
            if (spec.Concurrent)
                desired[spec.Name] = (spec, ConcurrentPortBase + residentIndex++);
            else if (spec.Name == swapSpec?.Name)
                desired[spec.Name] = (spec, _port);
        }

        foreach (var name in _own.Keys.Where(k => !desired.ContainsKey(k)).ToList())
        {
            await _own[name].StopAsync(ct);
            _own[name].Dispose();
            _own.Remove(name);
            _coordinator.Unregister(name);
        }

        foreach (var (name, (spec, p)) in desired)
        {
            _coordinator.Register(name, spec.Concurrent);

            if (!spec.Concurrent && _coordinator.IsGroupBusy("swap"))
                continue;

            if (_own.TryGetValue(name, out var backend) && backend.Port != p)
            {
                await backend.StopAsync(ct);
                backend.Dispose();
                _coordinator.Deactivate(name);
                _own.Remove(name);
                backend = null;
            }

            var cost = backend is not null ? Footprint(backend, spec) : VramEstimate.FromSpec(spec);
            if (!await TryAdmitAsync(ReservedExcept(name) + cost, ct))
                continue;

            if (backend is null)
            {
                backend = new LlamaServerBackend
                {
                    Name = name,
                    Port = p,
                    SkipRealProcess = SkipRealProcess,
                    Log = Log
                };
                _own[name] = backend;
            }
            try
            {
                await backend.StartAsync(spec, ct);
                if (backend.State == BackendState.Ready)
                    _coordinator.TryActivate(name);
            }
            catch (Exception)
            {
                // StartAsync already recorded the fault; the next tick retries.
            }
        }

        Status = BuildOwnStatus();
    }

    private LlamaSwapStatusDto BuildOwnStatus()
    {
        if (_own.Count == 0)
            return new LlamaSwapStatusDto(false, false, null, null, null, null);

        var backends = _own.Values.ToArray();
        var faulted = backends.Where(b => b.State == BackendState.Faulted).ToArray();
        var ready = backends.Where(b => b.State == BackendState.Ready).ToArray();
        var available = ready.Length > 0 || backends.Any(b => b.State == BackendState.Starting);
        var healthy = ready.Length > 0 && faulted.Length == 0;

        var active = ready.FirstOrDefault(b => b.Port == _port) ?? ready.FirstOrDefault();
        var loadedModel = active?.Name;
        if (loadedModel != null && loadedModel != _lastLoaded)
        {
            _lastLoaded = loadedModel;
            _lastSwap = DateTime.UtcNow;
        }

        var reported = backends.Where(b => b.VramFootprintMb > 0).Sum(b => b.VramFootprintMb);
        var anyActual = backends.Any(b => b.ActualVramMb > 0);
        var memory = (anyActual ? "" : "est ") + reported.ToString(CultureInfo.InvariantCulture) + " MiB";
        var error = faulted.Length > 0 ? faulted[0].Error : null;

        var loadedModels = backends
            .OrderBy(b => b.Port)
            .Select(b => new LoadedModelStatus(b.Name, b.State.ToString().ToLowerInvariant()))
            .ToList();

        return new LlamaSwapStatusDto(available, healthy, loadedModel, memory, _lastSwap, error, loadedModels);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var backend in _own.Values)
        {
            try { await backend.StopAsync(cts.Token); } catch { }
            backend.Dispose();
            _coordinator.Deactivate(backend.Name);
        }
        _coordinator.UnregisterAll();
        _own.Clear();
        await KillProcessAsync();
        _http.Dispose();
    }
}
