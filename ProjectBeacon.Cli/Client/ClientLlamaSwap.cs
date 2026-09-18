namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.LlamaSwap;

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
            bin = WorkstationActions.Which("llama-swap") ?? WorkstationActions.Which("llama-swap.exe");
        if (string.IsNullOrWhiteSpace(bin))
        {
            KillProcess();
            _lastHash = hash;
            _port = nextPort;
            _lastBin = null;
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap binary not found on this device.");
            return;
        }

        if (ShouldRestart(hash, _lastHash, nextPort, _port, bin, _lastBin, IsRunning))
            KillProcess();

        _lastHash = hash;
        _port = nextPort;
        _lastBin = bin;
        EnsureProcess(bin);
        await PollAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        KillProcess();
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
        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap failed to start.");
            return;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private void KillProcess()
    {
        if (SkipRealProcess)
        {
            _runningFake = false;
            return;
        }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(2000); } catch { }
        }
        _process?.Dispose();
        _process = null;
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

    public async ValueTask DisposeAsync()
    {
        KillProcess();
        _http.Dispose();
        await Task.CompletedTask;
    }
}
