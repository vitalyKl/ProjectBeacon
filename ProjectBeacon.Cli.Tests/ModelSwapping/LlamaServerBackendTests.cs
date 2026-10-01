namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class LlamaServerBackendTests : IDisposable
{
    private readonly List<LlamaServerBackend> _backends = [];

    private LlamaServerBackend NewBackend(int port = 8080)
    {
        var b = new LlamaServerBackend
        {
            Name = "test",
            Port = port,
            SkipRealProcess = true
        };
        _backends.Add(b);
        return b;
    }

    private static LlamaSwapModelSpec Spec(string name = "test") =>
        new(name, "llama-server -m test.gguf", 4096, 300, []);

    [Fact]
    public void InitialState_IsIdle()
    {
        var b = NewBackend();
        Assert.Equal(BackendState.Idle, b.State);
        Assert.Null(b.Error);
        Assert.Equal(0, b.VramFootprintMb);
    }

    [Fact]
    public async Task StartAsync_SkipRealProcess_TransitionsToReady()
    {
        var b = NewBackend();
        await b.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(BackendState.Ready, b.State);
        Assert.Null(b.Error);
        Assert.Equal(0, b.ActualVramMb);
        Assert.Equal(0, b.WorkingSetMb);
        Assert.Equal(4, b.EstimatedVramMb);
        Assert.Equal(b.EstimatedVramMb, b.VramFootprintMb);
    }

    [Fact]
    public async Task StartAsync_Idempotent_WhenAlreadyReady()
    {
        var b = NewBackend();
        await b.StartAsync(Spec(), CancellationToken.None);
        await b.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(BackendState.Ready, b.State);
    }

    [Fact]
    public async Task StopAsync_SkipRealProcess_TransitionsToIdle()
    {
        var b = NewBackend();
        await b.StartAsync(Spec(), CancellationToken.None);
        await b.StopAsync(CancellationToken.None);
        Assert.Equal(BackendState.Idle, b.State);
        Assert.Equal(0, b.VramFootprintMb);
    }

    [Fact]
    public async Task StartAsync_EmptyCommand_Faults()
    {
        var b = new LlamaServerBackend { Name = "bad", Port = 8080, SkipRealProcess = false };
        _backends.Add(b);
        await b.StartAsync(new LlamaSwapModelSpec("bad", "", 0, 0, []), CancellationToken.None);
        Assert.Equal(BackendState.Faulted, b.State);
        Assert.Contains("empty launch command", b.Error);
    }

    [Fact]
    public void Endpoint_EqualsPort()
    {
        var b = NewBackend(9123);
        Assert.Equal(9123, b.Endpoint);
    }

    public void Dispose()
    {
        foreach (var b in _backends)
            b.Dispose();
    }
}
