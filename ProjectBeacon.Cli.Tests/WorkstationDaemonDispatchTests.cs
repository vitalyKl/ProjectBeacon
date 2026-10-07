namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Enums;
using ProjectBeacon.Cli.Client;

public sealed class WorkstationDaemonDispatchTests : IDisposable
{
    private readonly string _dir;

    public WorkstationDaemonDispatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task InitProject_WithoutLocalRoot_ReturnsRuntimeRequired()
    {
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.InitProject,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(CommandSandbox.RuntimeRequired, error);
    }

    [Fact]
    public async Task ApplyOpencode_WithoutLocalRoot_ReturnsRuntimeRequired()
    {
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.ApplyOpencode,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(CommandSandbox.RuntimeRequired, error);
    }

    [Fact]
    public async Task RunReviewCheck_WithoutLocalRoot_ReturnsRuntimeRequired()
    {
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.RunReviewCheck,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(CommandSandbox.RuntimeRequired, error);
    }

    [Fact]
    public async Task InitProject_WithLocalRoot_CreatesProjectFiles()
    {
        var projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, result, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.InitProject,
            LocalRoot = projectRoot,
            PayloadJson = JsonSerializer.Serialize(new { path = ".", createGit = false })
        }, CancellationToken.None);
        Assert.True(ok, error);
        Assert.True(File.Exists(Path.Combine(projectRoot, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "opencode.json")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, ".opencode", "data")));
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal(projectRoot.Replace('\\', '/'), doc.RootElement.GetProperty("path").GetString()!.Replace('\\', '/'));
    }

    [Fact]
    public async Task InitProject_RejectsAbsolutePathInPayload()
    {
        var projectRoot = Path.Combine(_dir, "project2");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.InitProject,
            LocalRoot = projectRoot,
            PayloadJson = JsonSerializer.Serialize(new { path = Path.GetTempPath(), createGit = false })
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(WorkspacePath.RelativePathRequired, error);
    }

    [Fact]
    public async Task InitProject_RejectsParentSegmentInPayload()
    {
        var projectRoot = Path.Combine(_dir, "project3");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.InitProject,
            LocalRoot = projectRoot,
            PayloadJson = JsonSerializer.Serialize(new { path = "../escape", createGit = false })
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, error);
    }

    [Fact]
    public async Task ApplyOpencode_WithLocalRoot_WritesOpencodeJson()
    {
        var projectRoot = Path.Combine(_dir, "apply");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.ApplyOpencode,
            LocalRoot = projectRoot,
            PayloadJson = JsonSerializer.Serialize(new { path = ".", model = "llamacpp/test-model" })
        }, CancellationToken.None);
        Assert.True(ok, error);
        var opencodePath = Path.Combine(projectRoot, "opencode.json");
        Assert.True(File.Exists(opencodePath));
        var content = File.ReadAllText(opencodePath);
        Assert.Contains("test-model", content);
    }

    [Fact]
    public async Task ApplyOpencode_RejectsAbsolutePathInPayload()
    {
        var projectRoot = Path.Combine(_dir, "apply2");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.ApplyOpencode,
            LocalRoot = projectRoot,
            PayloadJson = JsonSerializer.Serialize(new { path = Path.GetTempPath(), model = "llamacpp/test" })
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(WorkspacePath.RelativePathRequired, error);
    }

    [Fact]
    public async Task SwapModel_CallsLlamaEnsureSwap()
    {
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(_dir, "config.yaml")
        };
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings());
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.SwapModel,
            PayloadJson = JsonSerializer.Serialize(new { model = "llamacpp/switched-model" })
        }, CancellationToken.None);
        Assert.True(ok, error);
    }

    [Fact]
    public async Task ChatAbort_RequiresExternalSessionId()
    {
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.ChatAbort,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("externalSessionId", error);
    }

    [Fact]
    public async Task ChatAbort_WithExternalSessionId_Succeeds()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        await using var daemon = new WorkstationDaemon(http, llama, () => new WorkstationSettings(), openCode: openCode);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.ChatAbort,
            PayloadJson = JsonSerializer.Serialize(new { externalSessionId = "oc-test" })
        }, CancellationToken.None);
        Assert.True(ok, error);
    }

    [Fact]
    public async Task ExecuteAsync_FullHttpPath_InitProject_ThroughCommandWire()
    {
        var projectRoot = Path.Combine(_dir, "http-project");
        Directory.CreateDirectory(projectRoot);
        var commandId = Guid.NewGuid();
        var pollCount = 0;
        var handler = new SimpleRouteHandler
        {
            Impl = req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("llamaswap-config", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.Contains("heartbeat", StringComparison.Ordinal))
                    return JsonResponse(new { id = Guid.Empty });
                if (path.Contains("/complete", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK);
                if (path.Contains("/commands", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref pollCount) == 1)
                    {
                        return JsonResponse(new
                        {
                            id = commandId,
                            kind = "InitProject",
                            localRoot = projectRoot,
                            payloadJson = JsonSerializer.Serialize(new { path = ".", createGit = false })
                        });
                    }
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = daemon.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (daemon.Snapshot.LastCommand is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        Assert.Equal("InitProject", daemon.Snapshot.LastCommand);
        Assert.True(daemon.Snapshot.LastCommandOk);
        Assert.True(File.Exists(Path.Combine(projectRoot, "opencode.json")));
    }

    [Fact]
    public async Task RunReviewCheck_WithLocalRoot_RequiresReviewRunId()
    {
        var projectRoot = Path.Combine(_dir, "review");
        Directory.CreateDirectory(projectRoot);
        await using var daemon = CreateDaemon(_dir);
        var (ok, _, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.RunReviewCheck,
            LocalRoot = projectRoot,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("reviewRunId", error);
    }

    [Fact]
    public async Task Probe_ExecutesWithoutLocalRoot()
    {
        await using var daemon = CreateDaemon(_dir);
        var (ok, result, error) = await daemon.ExecuteAsync(new WorkstationDaemon.CommandWire
        {
            Id = Guid.NewGuid(),
            Kind = WorkstationCommandKind.Probe,
            PayloadJson = "{}"
        }, CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("git", result);
    }

    private static WorkstationDaemon CreateDaemon(string tempDir)
    {
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            ConfigFile = Path.Combine(tempDir, "config.yaml")
        };
        return new WorkstationDaemon(http, llama, () => new WorkstationSettings());
    }

    private static HttpResponseMessage JsonResponse(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class SimpleRouteHandler : System.Net.Http.HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Impl { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Impl(request));
    }
}
