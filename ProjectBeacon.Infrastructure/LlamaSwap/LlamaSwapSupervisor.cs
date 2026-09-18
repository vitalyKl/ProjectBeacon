namespace ProjectBeacon.Infrastructure.LlamaSwap;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class LlamaSwapSupervisor : BackgroundService, ILlamaSwapProxy
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);
    private const int CrashLimit = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LlamaSwapOptions _options;
    private readonly ILogger<LlamaSwapSupervisor> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DateTime> _recentCrashes = new();

    private volatile LlamaSwapStatusDto _status;
    private Process? _process;
    private string _lastRegistryHash = string.Empty;
    private string? _lastLoadedModel;
    private DateTime? _lastSwap;

    public LlamaSwapSupervisor(
        IServiceScopeFactory scopeFactory,
        LlamaSwapOptions options,
        ILogger<LlamaSwapSupervisor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _status = InitialStatus();
    }

    public Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);

    public async Task<bool> ReloadAsync(CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            return false;
        try
        {
            var specs = await LoadRegistryAsync(ct);
            await WriteConfigFileAsync(specs, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "llama-swap config regeneration failed");
            SetStatus(healthy: false, error: $"config regeneration failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UnloadAsync(CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            return false;
        try
        {
            using var response = await _http.PostAsync(
                $"{BaseUrl}/api/models/unload", content: null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "llama-swap unload request failed");
            return false;
        }
    }

    private string BaseUrl => $"http://127.0.0.1:{_options.Port}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogInformation("llama-swap supervisor disabled: {Reason}", _status.Error);
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        EnsureConfigDirectory();
        try
        {
            var specs = await LoadRegistryAsync(stoppingToken);
            await WriteConfigFileAsync(specs, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "llama-swap initial config write failed; retrying on each tick");
        }

        StartProcess();

        using var timer = new PeriodicTimer(TickPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "llama-swap tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var specs = await LoadRegistryAsync(ct);
            var hash = HashRegistry(specs);
            if (hash != _lastRegistryHash)
                await WriteConfigFileAsync(specs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "llama-swap registry poll failed; keeping previous config");
        }

        EnsureProcessRunning();

        if (ProcessIsRunning())
            await PollProxyStatusAsync(ct);
        else
            SetStatus(healthy: false, error: "llama-swap process is not running.");
    }

    private async Task<IReadOnlyList<LlamaSwapModelSpec>> LoadRegistryAsync(CancellationToken ct)
    {
        var projectId = _options.ProjectId!.Value;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<BeaconDbContext>>()
            .CreateDbContext();
        await using (db)
        using (TenantScope.EnterProjectScope(projectId))
        {
            var backends = await db.LocalModelBackends
                .AsNoTracking()
                .OrderBy(b => b.Name)
                .ToListAsync(ct);
            return backends
                .Select(b => new LlamaSwapModelSpec(b.Name, b.LaunchCommand, b.ContextSize, b.Ttl, b.ExtraFlags, b.Concurrent))
                .ToList();
        }
    }

    private static string HashRegistry(IReadOnlyList<LlamaSwapModelSpec> specs)
    {
        var payload = string.Join("\n", specs.Select(s =>
            $"{s.Name}|{s.LaunchCommand}|{s.ContextSize}|{s.Ttl}|{s.Concurrent}|{string.Join(",", s.ExtraFlags)}"));
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task WriteConfigFileAsync(IReadOnlyList<LlamaSwapModelSpec> specs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = _options.ConfigPath!;
            EnsureConfigDirectory();
            var content = LlamaSwapConfigGenerator.Generate(specs);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
            _lastRegistryHash = HashRegistry(specs);
            _logger.LogInformation("llama-swap config written to {Path} ({Count} models)", path, specs.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureConfigDirectory()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_options.ConfigPath!));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private void EnsureProcessRunning()
    {
        if (ProcessIsRunning())
            return;

        var now = DateTime.UtcNow;
        lock (_recentCrashes)
        {
            _recentCrashes.RemoveAll(t => now - t > CrashWindow);
            if (_recentCrashes.Count >= CrashLimit)
            {
                SetStatus(healthy: false, error:
                    $"llama-swap crashed {_recentCrashes.Count} times in the last 60 seconds; supervisor stopped restarting it.");
                return;
            }
            _recentCrashes.Add(now);
        }

        StartProcess();
    }

    private bool ProcessIsRunning()
    {
        var p = _process;
        return p is { HasExited: false };
    }

    private void StartProcess()
    {
        _gate.Wait();
        try
        {
            if (ProcessIsRunning())
                return;

            var psi = new ProcessStartInfo
            {
                FileName = _options.BinPath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(_options.ConfigPath!);
            psi.ArgumentList.Add("-listen");
            psi.ArgumentList.Add($"127.0.0.1:{_options.Port}");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    _logger.LogDebug("llama-swap: {Line}", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    _logger.LogDebug("llama-swap stderr: {Line}", e.Data);
            };

            if (!process.Start())
            {
                _logger.LogWarning("llama-swap process failed to start");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _logger.LogInformation(
                "llama-swap started (pid {Pid}, config {Config}, listen 127.0.0.1:{Port})",
                process.Id, _options.ConfigPath, _options.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start llama-swap");
            SetStatus(healthy: false, error: $"llama-swap failed to start: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollProxyStatusAsync(CancellationToken ct)
    {
        string? loaded;
        try
        {
            using var health = await _http.GetAsync($"{BaseUrl}/health", ct);
            if (!health.IsSuccessStatusCode)
            {
                SetStatus(healthy: false, error: $"llama-swap /health returned {(int)health.StatusCode}");
                return;
            }
            loaded = await FetchLoadedModelsAsync(ct);
        }
        catch (Exception ex)
        {
            SetStatus(healthy: false, error: $"llama-swap not reachable: {ex.Message}");
            return;
        }

        if (loaded is not null && loaded != _lastLoadedModel)
            _lastSwap = DateTime.UtcNow;
        _lastLoadedModel = loaded;

        var memory = await PollMemoryAsync(ct);
        SetStatus(healthy: true, loadedModel: loaded, memory: memory, error: null);
    }

    private async Task<string?> FetchLoadedModelsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/running", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("running", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "llama-swap /running parse failed");
            return null;
        }
    }

    private async Task<string?> PollMemoryAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{BaseUrl}/metrics", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var text = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var idx = trimmed.LastIndexOf(' ');
                if (idx <= 0 || idx == trimmed.Length - 1)
                    continue;

                var metricPart = trimmed[..idx];
                var valuePart = trimmed[(idx + 1)..];
                var nameEnd = metricPart.IndexOf('{');
                var name = (nameEnd < 0 ? metricPart : metricPart[..nameEnd]).Trim();
                if (!Regex.IsMatch(name, "(?i)memory|vram|rss|resident"))
                    continue;
                if (!double.TryParse(valuePart, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    continue;

                return FormatMemory(value);
            }
        }
        catch
        {
            // /metrics can 503 when performance monitoring is disabled.
        }
        return null;
    }

    private static string FormatMemory(double value)
    {
        const double mb = 1024d * 1024d;
        const double gb = mb * 1024d;
        var inv = CultureInfo.InvariantCulture;
        if (value >= gb)
            return (value / gb).ToString("0.00", inv) + " GB";
        if (value >= mb)
            return (value / mb).ToString("0.00", inv) + " MB";
        return value.ToString("0", inv) + " B";
    }

    private void SetStatus(bool healthy, string? loadedModel = null, string? memory = null, string? error = null)
    {
        _status = new LlamaSwapStatusDto(true, healthy, loadedModel, memory, _lastSwap, error);
    }

    private LlamaSwapStatusDto InitialStatus()
    {
        if (string.IsNullOrWhiteSpace(_options.BinPath))
            return new LlamaSwapStatusDto(false, false, null, null, null,
                "llama-swap binary not configured (set BEACON_LLAMASWAP_BIN).");
        if (_options.ProjectId is null)
            return new LlamaSwapStatusDto(false, false, null, null, null,
                "llama-swap project not configured (set BEACON_PROJECT_ID).");
        return new LlamaSwapStatusDto(true, false, null, null, null, "llama-swap is starting.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _gate.Wait();
        try
        {
            var process = _process;
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill llama-swap process tree");
                }
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    // Process did not exit in time; move on.
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        _http.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
