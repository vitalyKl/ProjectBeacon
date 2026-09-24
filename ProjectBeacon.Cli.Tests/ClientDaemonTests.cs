namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Domain.Enums;
using ProjectBeacon.Cli.Client;

public sealed class ClientDaemonTests : IDisposable
{
    private readonly string _dir;

    public ClientDaemonTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void NeedsWizard_WhenMissingCredentials()
    {
        Assert.True(ClientStore.NeedsWizard(new ClientStore()));
        Assert.False(ClientStore.NeedsWizard(new ClientStore { Url = "http://localhost:5083", Token = "bcd_x" }));
    }

    [Fact]
    public void ClientOptions_ParsesHeadlessAndEnroll()
    {
        var headless = ClientOptions.Parse(["client", "--headless", "--url", "http://x"]);
        Assert.True(headless.Headless);
        Assert.Equal("http://x", headless.Url);

        var enroll = ClientOptions.Parse(["client", "enroll", "--url", "http://x", "--login", "a", "--password", "b"]);
        Assert.True(enroll.Enroll);
        Assert.Equal("a", enroll.Login);
        Assert.Equal("b", enroll.Password);
    }

    [Fact]
    public void Autostart_WritesAndRemovesScript()
    {
        WindowsAutostart.Enable(_dir, @"C:\beacon.exe", "client");
        var path = Path.Combine(_dir, WindowsAutostart.ShortcutName);
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("@echo off", text);
        Assert.Contains(@"C:\beacon.exe", text);
        Assert.Contains("client", text);
        Assert.True(WindowsAutostart.IsEnabled(_dir));
        WindowsAutostart.Disable(_dir);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Autostart_RenderScript_QuotesFile()
    {
        var script = WindowsAutostart.RenderScript(@"C:\Program Files\dotnet\dotnet.exe", "\"app.dll\" client");
        Assert.StartsWith("@echo off", script);
        Assert.Contains("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"app.dll\" client", script);
    }

    [Fact]
    public async Task SetControlPlane_UpdatesHttpBaseAndSnapshot()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://old.example/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings());
        Assert.Equal("http://old.example", daemon.Snapshot.Url);
        daemon.SetControlPlane("http://localhost:5083");
        Assert.Equal("http://localhost:5083/", http.BaseAddress!.ToString());
        Assert.Equal("http://localhost:5083", daemon.Snapshot.Url);
    }

    [Fact]
    public void HostLoad_ParsesNvidiaSmiAndFormatsMemory()
    {
        var gpu = HostLoadSampler.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 12, 1024, 24564\n");
        Assert.NotNull(gpu);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpu!.Name);
        Assert.Equal(12, gpu.UtilizationPercent);
        Assert.Equal(1024L * 1024 * 1024, gpu.MemoryUsedBytes);
        Assert.Equal(24564L * 1024 * 1024, gpu.MemoryTotalBytes);
        Assert.Equal("2.00 MB", HostLoadSampler.FormatMemory("2097152"));
        Assert.Equal("1.00 GB", HostLoadSampler.FormatMemory("1073741824"));
    }

    [Fact]
    public void LlamaSwap_ShouldRestart_OnHashPortBinOrDead()
    {
        Assert.True(ClientLlamaSwap.ShouldRestart("a", "a", 8080, 8080, "bin", "bin", running: false));
        Assert.False(ClientLlamaSwap.ShouldRestart("a", "a", 8080, 8080, "bin", "bin", running: true));
        Assert.True(ClientLlamaSwap.ShouldRestart("b", "a", 8080, 8080, "bin", "bin", running: true));
        Assert.True(ClientLlamaSwap.ShouldRestart("a", "a", 9090, 8080, "bin", "bin", running: true));
        Assert.True(ClientLlamaSwap.ShouldRestart("a", "a", 8080, 8080, "other", "bin", running: true));
    }

    [Fact]
    public async Task LlamaSwap_TickAndReload_RestartsProcess()
    {
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await llama.TickAsync("models: {}\n", 8080, "fake-bin", CancellationToken.None);
        Assert.Equal(1, llama.StartCount);
        await llama.TickAsync("models: {}\n", 8080, "fake-bin", CancellationToken.None);
        Assert.Equal(1, llama.StartCount);
        await llama.TickAsync("models: { a: 1 }\n", 8080, "fake-bin", CancellationToken.None);
        Assert.Equal(2, llama.StartCount);
        await llama.TickAsync("models: { a: 1 }\n", 9090, "fake-bin", CancellationToken.None);
        Assert.Equal(3, llama.StartCount);
        await llama.ReloadAsync(CancellationToken.None);
        Assert.Equal(4, llama.StartCount);
    }

    [Fact]
    public async Task Enroll_ReturnsToken_OnLoginAndCreate()
    {
        var handler = new RouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/v1/auth/login", StringComparison.Ordinal))
                    return Json(new { token = "jwt" });
                if (path.EndsWith("/v1/devices", StringComparison.Ordinal))
                    return Json(new { id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), token = "bcd_test" });
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };
        var result = await ClientEnrollment.EnrollAsync("http://localhost:5083", "u", "p", "pc", CancellationToken.None, handler);
        Assert.True(result.Ok);
        Assert.Equal("bcd_test", result.Token);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), result.Id);
    }

    [Fact]
    public async Task Enroll_Fails_OnBadLogin()
    {
        var handler = new RouteHandler
        {
            Impl = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        var result = await ClientEnrollment.EnrollAsync("http://localhost:5083", "u", "p", "pc", CancellationToken.None, handler);
        Assert.False(result.Ok);
        Assert.Contains("Login", result.Error);
    }

    [Fact]
    public async Task ProbeUrl_ReportsStatus()
    {
        var handler = new RouteHandler
        {
            Impl = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        };
        var (ok, message) = await ClientEnrollment.ProbeUrlAsync("http://localhost:5083", CancellationToken.None, handler);
        Assert.True(ok);
        Assert.Contains("200", message);
    }

    [Fact]
    public async Task Daemon_HeartbeatsThenCancels()
    {
        var handler = new RouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.Contains("heartbeat", StringComparison.Ordinal))
                    return Json(new { id = Guid.Empty });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings())
        {
            DelayAsync = (_, ct) => Task.Delay(1, ct),
            HeartbeatInterval = TimeSpan.Zero,
            CommandErrorDelay = TimeSpan.Zero
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await daemon.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(handler.Heartbeats >= 1);
        Assert.True(daemon.Snapshot.Connected);
    }

    [Fact]
    public async Task Daemon_ExecutesProbeAndCompletes()
    {
        var commandId = Guid.NewGuid();
        var sent = 0;
        var handler = new RouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.Contains("heartbeat", StringComparison.Ordinal))
                    return Json(new { id = Guid.Empty });
                if (path.Contains("/complete", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK);
                if (path.Contains("/commands", StringComparison.Ordinal) && Interlocked.Increment(ref sent) == 1)
                {
                    return Json(new
                    {
                        id = commandId,
                        kind = "Probe",
                        payloadJson = "{}"
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings())
        {
            DelayAsync = (_, ct) => Task.Delay(1, ct)
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = daemon.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (handler.Completes == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        Assert.True(handler.Completes >= 1);
        Assert.Equal("Probe", daemon.Snapshot.LastCommand);
        Assert.True(daemon.Snapshot.LastCommandOk);
    }

    [Fact]
    public async Task ExecuteAsync_ReloadProxy_RestartsLlama()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await llama.TickAsync("models: {}\n", 8080, "fake-bin", CancellationToken.None);
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings());
        var (ok, _, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire { Id = Guid.NewGuid(), Kind = WorkstationCommandKind.ReloadProxy, PayloadJson = "{}" },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Equal(2, llama.StartCount);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_CompletesOnIdle()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        using var http = new HttpClient(new RouteHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(1),
            ChatMaxDuration = TimeSpan.FromSeconds(180)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                PayloadJson = JsonSerializer.Serialize(new { path = _dir, chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("idle", string.Join("\n", logs));
        Assert.Contains("\"interrupted\":false", result);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_InterruptsOnMaxDuration()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        using var http = new HttpClient(new RouteHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(10),
            ChatMaxDuration = TimeSpan.FromSeconds(1)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                PayloadJson = JsonSerializer.Serialize(new { path = _dir, chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("interrupted", string.Join("\n", logs));
        Assert.Contains("\"interrupted\":true", result);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class RouteHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Impl { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        public int Heartbeats;
        public int Completes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("heartbeat", StringComparison.Ordinal))
                Interlocked.Increment(ref Heartbeats);
            if (path.Contains("/complete", StringComparison.Ordinal))
                Interlocked.Increment(ref Completes);
            return Task.FromResult(Impl(request));
        }
    }
}
