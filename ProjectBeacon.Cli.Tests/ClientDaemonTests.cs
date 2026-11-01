namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application.Common;
using Application.Devices;
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
    public async Task SetToken_UpdatesBearerHeaderAndResetsConnection()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:5083/"),
            DefaultRequestHeaders = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bcd_old") }
        };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings());
        daemon.SetToken("bcd_new");
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("bcd_new", http.DefaultRequestHeaders.Authorization.Parameter);
        Assert.False(daemon.Snapshot.Connected);
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
    public async Task LlamaSwap_TickAsync_PortBusy_SkipsLaunchAndReportsHolder()
    {
        await using var llama = new ClientLlamaSwap
        {
            PortHolderProbe = _ => 4242,
            ConfigFile = Path.Combine(_dir, "config-busy.yaml")
        };
        await llama.TickAsync("models: {}\n", 9999, "fake-bin", CancellationToken.None);
        Assert.Equal(0, llama.StartCount);
        Assert.False(llama.Status.Healthy);
        Assert.Contains("4242", llama.Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in use", llama.Status.Error, StringComparison.OrdinalIgnoreCase);
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
            CommandsHold = TimeSpan.FromSeconds(2),
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
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), null, openCode)
        {
            // A real (non-zero) interval keeps the daemon from spawning a fresh nvidia-smi
            // process every loop turn, which would starve the polling below on loaded hosts.
            HeartbeatInterval = TimeSpan.FromMilliseconds(150)
        };
        // The first heartbeat may take up to ~55 s on a loaded host (eleven bounded
        // `where` launches in WorkstationProbe), so wait past that instead of 10 s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var run = daemon.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (handler.Heartbeats < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        Assert.True(handler.Heartbeats >= 1);
        Assert.True(daemon.Snapshot.Connected);
    }

    [Fact]
    public async Task Daemon_Heartbeat_InvalidSettingsFile_KeepsHeartbeatAliveAndReportsUnavailableRuntime()
    {
        var badSettings = Path.Combine(_dir, "bad-workstation.json");
        File.WriteAllText(badSettings, "<html></html>");
        string? heartbeatPayload = null;
        var handler = new RouteHandler
        {
            CommandsHold = TimeSpan.FromMilliseconds(20),
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.Contains("heartbeat", StringComparison.Ordinal))
                {
                    heartbeatPayload = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    return Json(new { id = Guid.Empty });
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var daemon = new WorkstationDaemon(http, llama, () => WorkstationSettings.Load(badSettings))
        {
            DelayAsync = (_, ct) => Task.Delay(1, ct),
            // A real (non-zero) interval keeps the daemon from spawning a fresh nvidia-smi
            // process every loop turn, which would starve the polling below on loaded hosts.
            HeartbeatInterval = TimeSpan.FromMilliseconds(150),
            CommandErrorDelay = TimeSpan.Zero
        };
        // The first heartbeat may take up to ~55 s on a loaded host (eleven bounded
        // `where` launches in WorkstationProbe), so wait past that instead of 10 s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var run = daemon.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while ((!daemon.Snapshot.Connected || handler.Heartbeats < 1) && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; }
        catch (OperationCanceledException) { }

        Assert.True(handler.Heartbeats >= 1);
        Assert.True(daemon.Snapshot.Connected);
        Assert.NotNull(heartbeatPayload);
        using var heartbeat = JsonDocument.Parse(heartbeatPayload);
        Assert.Equal(JsonValueKind.Null, heartbeat.RootElement.GetProperty("workstationJson").ValueKind);
        var probeJson = heartbeat.RootElement.GetProperty("probeJson").GetString();
        Assert.False(string.IsNullOrWhiteSpace(probeJson));
        using var probe = JsonDocument.Parse(probeJson!);
        var runtime = probe.RootElement.GetProperty("agentRuntime");
        Assert.Equal("unavailable", runtime.GetProperty("runtime").GetString());
        Assert.False(runtime.GetProperty("healthy").GetBoolean());
        Assert.Contains(badSettings, runtime.GetProperty("error").GetString());
        Assert.Contains("Delete the file", runtime.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Daemon_ExecutesProbeAndCompletes()
    {
        var commandId = Guid.NewGuid();
        var sent = 0;
        var handler = new RouteHandler
        {
            // Mimics the real server's long-poll (?wait=25). Without the hold the
            // daemon tight-repolls 204s after the probe, flooding the thread pool
            // and starving the polling below on loaded hosts.
            CommandsHold = TimeSpan.FromSeconds(2),
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
            DelayAsync = (_, ct) => Task.Delay(1, ct),
            // Heartbeat work (llama sync, host/GPU sampling) runs in parallel with the
            // command loop and starves the claim->complete cycle on loaded CI hosts.
            // The test asserts no heartbeat state, so keep that loop quiet for the window.
            HeartbeatInterval = TimeSpan.FromSeconds(60)
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = daemon.RunAsync(cts.Token);
        // On a loaded 2-core GHA host every continuation in the cycle can be delayed
        // seconds, so the claim->execute->complete->publish cycle can exceed a tight
        // deadline even though nothing is broken. Healthy hosts finish in seconds,
        // because the loop exits as soon as the snapshot is published.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (daemon.Snapshot.LastCommand is null && DateTime.UtcNow < deadline)
            await Task.Delay(100, TestContext.Current.CancellationToken);
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
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { path = ".", chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("idle", string.Join("\n", logs));
        Assert.Contains("\"interrupted\":false", result);
        Assert.Equal(0, openCode.AbortCount);
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
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("interrupted", string.Join("\n", logs));
        Assert.Contains("\"interrupted\":true", result);
        Assert.Equal(1, openCode.AbortCount);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_RejectsAbsolutePathAndEscape()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings());
        var absolute = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Kind = WorkstationCommandKind.ChatPrompt,
            LocalRoot = _dir,
            PayloadJson = JsonSerializer.Serialize(new { path = _dir, chatSessionId = Guid.NewGuid(), externalSessionId = "oc-1", text = "hello" })
        }, CancellationToken.None);
        Assert.False(absolute.Ok);
        Assert.Contains("relative", absolute.Error);

        var escape = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Kind = WorkstationCommandKind.ChatEnsureSession,
            LocalRoot = _dir,
            PayloadJson = JsonSerializer.Serialize(new { path = "../secret", title = "Chat" })
        }, CancellationToken.None);
        Assert.False(escape.Ok);
        Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, escape.Error);

        var missing = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Kind = WorkstationCommandKind.RunEvalTurn,
            PayloadJson = """{"evalRunId":"00000000-0000-0000-0000-000000000001","prompt":"hi"}"""
        }, CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Equal(CommandSandbox.RuntimeRequired, missing.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ListDir_UsesLocalProjectsRoot_NotPayloadRoot()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        var settings = new WorkstationSettings { ProjectsRoot = _dir, ModelsRoot = _dir };
        await using var daemon = new WorkstationDaemon(http, llama, () => settings);
        var outside = Path.Combine(Path.GetTempPath(), "beacon-payload-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var listed = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
            {
                Kind = WorkstationCommandKind.ListDir,
                PayloadJson = JsonSerializer.Serialize(new { path = _dir, root = outside })
            }, CancellationToken.None);
            Assert.True(listed.Ok, listed.Error);

            var rejected = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
            {
                Kind = WorkstationCommandKind.ListDir,
                PayloadJson = JsonSerializer.Serialize(new { path = outside, root = _dir })
            }, CancellationToken.None);
            Assert.False(rejected.Ok);
            Assert.Contains("escapes", rejected.Error);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public async Task LlamaSync_ConcurrentSyncs_Serialize()
    {
        var handler = new RouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return Json(new { yaml = "models: {}\n", port = 8080 });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        var state = new DaemonRuntimeState("http://localhost", null);
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        var deps = new DaemonDependencies(http, llama, openCode, new OpenCodeAgentRuntime(openCode), () => new WorkstationSettings());
        var sync = new DaemonLlamaSync(deps, state);
        try
        {
            var tasks = Enumerable.Range(0, 5).Select(_ => sync.SyncAsync(new WorkstationSettings(), CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            sync.Release();
        }
        Assert.Equal(1, llama.StartCount);
    }

    [Fact]
    public async Task LlamaSync_SyncNow_UsesSameLock()
    {
        var handler = new RouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return Json(new { yaml = "models: {}\n", port = 8080 });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        var state = new DaemonRuntimeState("http://localhost", null);
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        var deps = new DaemonDependencies(http, llama, openCode, new OpenCodeAgentRuntime(openCode), () => new WorkstationSettings());
        var sync = new DaemonLlamaSync(deps, state);
        try
        {
            await sync.SyncNowAsync(CancellationToken.None);
            Assert.Equal(1, llama.StartCount);
            await sync.SyncNowAsync(CancellationToken.None);
            Assert.Equal(1, llama.StartCount);
        }
        finally
        {
            sync.Release();
        }
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class RouteHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Impl { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        // When non-zero, the /commands path holds the request this long, mimicking the real
        // server's long-poll (?wait=25). Without it the fake returns NoContent instantly and
        // the daemon's command loop tight-re-polls, starving other continuations in-process.
        public TimeSpan CommandsHold { get; set; } = TimeSpan.Zero;

        public int Heartbeats;
        public int Completes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("heartbeat", StringComparison.Ordinal))
                Interlocked.Increment(ref Heartbeats);
            if (path.Contains("/complete", StringComparison.Ordinal))
                Interlocked.Increment(ref Completes);
            if (CommandsHold > TimeSpan.Zero && path.Contains("/commands", StringComparison.Ordinal))
                await Task.Delay(CommandsHold, cancellationToken);
            return Impl(request);
        }
    }
    [Fact]
    public async Task ExecuteAsync_ChatPrompt_WaitsThroughToolCallsUntilStop()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        var capture = new ChatCaptureHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        var toolCalls = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"tool-calls","content":[{"type":"reasoning","id":"p1","text":"thinking","time":{"created":1,"completed":2}}]}]
            """;
        var streaming = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"tool-calls","content":[{"type":"reasoning","id":"p1","text":"thinking","time":{"created":1,"completed":2}}]},{"type":"message","role":"assistant","id":"m2","time":{"created":3},"content":[{"type":"text","id":"p2","text":"partial","time":{"created":3}}]}]
            """;
        var finished = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"tool-calls","content":[{"type":"reasoning","id":"p1","text":"thinking","time":{"created":1,"completed":2}}]},{"type":"message","role":"assistant","id":"m2","time":{"created":3,"completed":5},"finish":"stop","content":[{"type":"text","id":"p2","text":"the answer","time":{"created":3,"completed":5}}]}]
            """;
        var messageGets = 0;
        await using var openCode = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri?.Query ?? "";
                if (path == "/api/info")
                    return Task.FromResult(OkJson("{\"version\":\"2.1.0\"}"));
                if (request.Method == HttpMethod.Get && path.EndsWith("/message", StringComparison.Ordinal))
                {
                    var n = Interlocked.Increment(ref messageGets);
                    var json = n <= 3 ? toolCalls : n <= 5 ? streaming : finished;
                    return Task.FromResult(OkJson(query.Contains("order=desc", StringComparison.Ordinal) ? DescPage(json) : json));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };
        openCode.HandleStdoutLine("server password test-pw");
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(10),
            ChatMaxDuration = TimeSpan.FromSeconds(60)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { path = ".", chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Equal(2, capture.PartBodies.Count);
        Assert.Contains("thinking", capture.PartBodies[0]);
        Assert.Contains("the answer", capture.PartBodies[1]);
        Assert.DoesNotContain("partial", string.Join("|", capture.PartBodies));
        Assert.Equal(1, capture.IdlePosts);
        Assert.Contains("\"interrupted\":false", result);
        Assert.Contains("idle", string.Join("\n", logs));
        Assert.Equal(8, messageGets);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_RunsToMaxDurationWhenNoAssistantMessageAppears()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        var capture = new ChatCaptureHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        await using var openCode = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/info")
                    return Task.FromResult(OkJson("{\"version\":\"2.1.0\"}"));
                if (request.Method == HttpMethod.Get && path.EndsWith("/message", StringComparison.Ordinal))
                    return Task.FromResult(OkJson("[{\"role\":\"user\",\"text\":\"hello\"}]"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(1),
            ChatMaxDuration = TimeSpan.FromSeconds(2)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Empty(capture.PartBodies);
        Assert.Equal(1, capture.IdlePosts);
        // No assistant part ever arrived, so the quiet path must not idle the turn: it ran to the
        // max duration and was aborted (the final idle marker is still posted).
        Assert.Contains("\"interrupted\":true", result);
        Assert.Contains("max duration", string.Join("\n", logs));
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_ErrorTurn_PostsErrorPart()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        var capture = new ChatCaptureHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        var errorTurn = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"error","error":{"name":"ContextOverflow","message":"request (12099 tokens) exceeds the available context size (10240 tokens)"}}]
            """;
        var messageGets = 0;
        await using var openCode = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri?.Query ?? "";
                if (path == "/api/info")
                    return Task.FromResult(OkJson("{\"version\":\"2.1.0\"}"));
                if (request.Method == HttpMethod.Get && path.EndsWith("/message", StringComparison.Ordinal))
                {
                    var n = Interlocked.Increment(ref messageGets);
                    var json = n <= 1 ? "[]" : errorTurn;
                    return Task.FromResult(OkJson(query.Contains("order=desc", StringComparison.Ordinal) ? DescPage(json) : json));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(10),
            ChatMaxDuration = TimeSpan.FromSeconds(60)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { path = ".", chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Single(capture.PartBodies);
        Assert.Contains("\"kind\":\"error\"", capture.PartBodies[0]);
        Assert.Contains("request (12099 tokens) exceeds", capture.PartBodies[0]);
        Assert.Equal(1, capture.IdlePosts);
        Assert.Contains("\"interrupted\":false", result);
        Assert.Contains("idle with error", string.Join("\n", logs));
        Assert.Equal(4, messageGets);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPrompt_StaleErrorTurn_WaitsForNewTurn()
    {
        var chatId = Guid.NewGuid();
        var logs = new List<string>();
        var capture = new ChatCaptureHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        var staleError = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"error","error":{"message":"Step interrupted"}}]
            """;
        var newTurnRunning = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"error","error":{"message":"Step interrupted"}},{"type":"message","role":"assistant","id":"m2","time":{"created":3},"content":[{"type":"text","id":"p2","text":"partial","time":{"created":3}}]}]
            """;
        var newTurnDone = """
            [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"error","error":{"message":"Step interrupted"}},{"type":"message","role":"assistant","id":"m2","time":{"created":3,"completed":5},"finish":"stop","content":[{"type":"text","id":"p2","text":"the answer","time":{"created":3,"completed":5}}]}]
            """;
        var messageGets = 0;
        await using var openCode = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri?.Query ?? "";
                if (path == "/api/info")
                    return Task.FromResult(OkJson("{\"version\":\"2.1.0\"}"));
                if (request.Method == HttpMethod.Get && path.EndsWith("/message", StringComparison.Ordinal))
                {
                    var n = Interlocked.Increment(ref messageGets);
                    var json = n <= 3 ? staleError : n <= 5 ? newTurnRunning : newTurnDone;
                    return Task.FromResult(OkJson(query.Contains("order=desc", StringComparison.Ordinal) ? DescPage(json) : json));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), m => logs.Add(m), openCode)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            ChatIdleTimeout = TimeSpan.FromSeconds(10),
            ChatMaxDuration = TimeSpan.FromSeconds(60)
        };
        var (ok, result, error) = await daemon.ExecuteAsync(
            new WorkstationDaemon.CommandWire
            {
                Id = Guid.NewGuid(),
                Kind = WorkstationCommandKind.ChatPrompt,
                LocalRoot = _dir,
                PayloadJson = JsonSerializer.Serialize(new { path = ".", chatSessionId = chatId, externalSessionId = "oc-1", text = "hello" })
            },
            CancellationToken.None);
        Assert.True(ok, error);
        Assert.Single(capture.PartBodies);
        Assert.Contains("the answer", capture.PartBodies[0]);
        Assert.DoesNotContain("Step interrupted", string.Join("|", capture.PartBodies));
        Assert.DoesNotContain("\"kind\":\"error\"", string.Join("|", capture.PartBodies));
        Assert.Equal(1, capture.IdlePosts);
        Assert.Contains("\"interrupted\":false", result);
        Assert.Equal(8, messageGets);
    }

    // The v2 server lists newest-first when ?order=desc is requested; the chat fixtures are authored oldest-first.
    private static string DescPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<string>();
        foreach (var element in doc.RootElement.EnumerateArray())
            items.Add(element.GetRawText());
        var reversed = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
            reversed[i] = items[items.Count - 1 - i];
        return "[" + string.Join(",", reversed) + "]";
    }

    private static HttpResponseMessage OkJson(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ChatCaptureHandler : HttpMessageHandler
    {
        public List<string> PartBodies { get; } = new();
        public int IdlePosts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/parts", StringComparison.Ordinal))
                PartBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            else if (path.EndsWith("/idle", StringComparison.Ordinal))
                IdlePosts++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
