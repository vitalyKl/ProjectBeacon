namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

[Trait("Lane", "Gpu")]
public sealed class LlamaServerGpuTests : IDisposable
{
    private readonly List<LlamaServerBackend> _backends = [];

    [Fact]
    public async Task StartAsync_RealBinary_ReachesReady()
    {
        var backend = new LlamaServerBackend
        {
            Name = "gpu-lane",
            Port = 18181,
            SkipRealProcess = false
        };
        _backends.Add(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await backend.StartAsync(new LlamaSwapModelSpec("gpu-lane", "llama-server -m test.gguf", 512, 0, []), cts.Token);
        Assert.Equal(BackendState.Ready, backend.State);
    }

    public void Dispose()
    {
        foreach (var backend in _backends)
            backend.Dispose();
    }
}
