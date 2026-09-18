namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Net.Http.Json;
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
    private int _port = 8080;
    private DateTime? _lastSwap;
    private string? _lastLoaded;

    public LlamaSwapStatusDto Status { get; private set; } =
        new(false, false, null, null, null, "llama-swap is not started on this device.");

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectBeacon", "llama-swap", "config.yaml");

    public async Task TickAsync(string yaml, int port, string? binPath, CancellationToken ct)
    {
        _port = port <= 0 ? 8080 : port;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
        if (hash != _lastHash)
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var tmp = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, ConfigPath, overwrite: true);
            _lastHash = hash;
        }

        var bin = binPath;
        if (string.IsNullOrWhiteSpace(bin))
            bin = WorkstationActions.Which("llama-swap") ?? WorkstationActions.Which("llama-swap.exe");
        if (string.IsNullOrWhiteSpace(bin))
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap binary not found on this device.");
            return;
        }

        EnsureProcess(bin);
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
        memory = Status.Memory,
        lastSwap = Status.LastSwap,
        error = Status.Error
    };

    private void EnsureProcess(string bin)
    {
        if (_process is { HasExited: false })
            return;
        var psi = new ProcessStartInfo
        {
            FileName = bin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(ConfigPath);
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

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var health = await _http.GetAsync($"http://127.0.0.1:{_port}/health", ct);
            if (!health.IsSuccessStatusCode)
            {
                Status = new LlamaSwapStatusDto(true, false, null, null, _lastSwap, $"/health {(int)health.StatusCode}");
                return;
            }
            var loaded = await FetchLoadedAsync(ct);
            if (loaded is not null && loaded != _lastLoaded)
                _lastSwap = DateTime.UtcNow;
            _lastLoaded = loaded;
            Status = new LlamaSwapStatusDto(true, true, loaded, await FetchMemoryAsync(ct), _lastSwap, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new LlamaSwapStatusDto(true, false, null, null, _lastSwap, $"llama-swap not reachable: {ex.Message}");
        }
    }

    private async Task<string?> FetchLoadedAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://127.0.0.1:{_port}/running", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("running", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var names = new List<string>();
            foreach (var element in arr.EnumerateArray())
            {
                var state = element.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state is not ("ready" or "starting" or "loaded" or "loading"))
                    continue;
                var model = element.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(model))
                    names.Add(model!);
            }
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        catch
        {
            return null;
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
                return trimmed[(idx + 1)..].Trim();
            }
        }
        catch
        {
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _http.Dispose();
    }
}
