namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class LlamaServerBackendLogTests : IDisposable
{
    private readonly List<string> _lines = [];
    private readonly LlamaServerBackend _backend;

    public LlamaServerBackendLogTests()
    {
        _backend = new LlamaServerBackend
        {
            Name = "log-test",
            Port = 8080,
            SkipRealProcess = true,
            Log = msg => _lines.Add(msg)
        };
    }

    [Fact]
    public async Task StartSkip_LogsSuccess()
    {
        var spec = new LlamaSwapModelSpec("log-test", "llama-server -m x.gguf", 4096, 300, []);
        await _backend.StartAsync(spec, CancellationToken.None);
        Assert.Contains(_lines, l => l.Contains("start (skip)"));
    }

    [Fact]
    public async Task StopSkip_LogsStop()
    {
        var spec = new LlamaSwapModelSpec("log-test", "llama-server -m x.gguf", 4096, 300, []);
        await _backend.StartAsync(spec, CancellationToken.None);
        _lines.Clear();
        await _backend.StopAsync(CancellationToken.None);
        Assert.Contains(_lines, l => l.Contains("stop"));
    }

    [Fact]
    public async Task StartEmptyCommand_LogsFailure()
    {
        var b = new LlamaServerBackend
        {
            Name = "empty-cmd",
            Port = 8081,
            SkipRealProcess = false,
            Log = msg => _lines.Add(msg)
        };
        var spec = new LlamaSwapModelSpec("empty-cmd", "", 0, 0, []);
        await b.StartAsync(spec, CancellationToken.None);
        Assert.Contains(_lines, l => l.Contains("start FAILED") && l.Contains("empty launch command"));
        b.Dispose();
    }

    public void Dispose() => _backend.Dispose();
}
