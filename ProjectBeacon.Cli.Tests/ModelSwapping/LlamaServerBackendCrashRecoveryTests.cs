namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class LlamaServerBackendCrashRecoveryTests : IDisposable
{
    private readonly List<LlamaServerBackend> _backends = [];

    private LlamaServerBackend NewBackend(int port = 8080, bool skipReal = true)
    {
        var b = new LlamaServerBackend
        {
            Name = "test",
            Port = port,
            SkipRealProcess = skipReal
        };
        _backends.Add(b);
        return b;
    }

    private static LlamaSwapModelSpec Spec(string name = "test") =>
        new(name, "llama-server -m test.gguf", 4096, 300, []);

    [Fact]
    public void RestartCount_InitiallyZero()
    {
        var b = NewBackend();
        Assert.Equal(0, b.RestartCount);
    }

    [Fact]
    public void FaultedState_AllowsRetryToStarting()
    {
        var fsm = new ModelBackendFsm();
        fsm.TryTransition(BackendState.Starting);
        fsm.TryTransition(BackendState.Faulted, "crash");

        Assert.True(fsm.TryTransition(BackendState.Starting));
        Assert.Equal(BackendState.Starting, fsm.Current);
    }

    [Fact]
    public async Task StartAsync_FromFaultedState_Retries()
    {
        var b = new LlamaServerBackend
        {
            Name = "retry-test",
            Port = 8080,
            SkipRealProcess = false
        };
        _backends.Add(b);

        await b.StartAsync(new LlamaSwapModelSpec("retry-test", "", 0, 0, []), CancellationToken.None);
        Assert.Equal(BackendState.Faulted, b.State);
        Assert.Contains("empty launch command", b.Error);

        await b.StartAsync(Spec("retry-test"), CancellationToken.None);
        Assert.Equal(BackendState.Ready, b.State);
    }

    [Fact]
    public async Task StartAsync_EmptyCommand_FaultsOnEachAttempt()
    {
        var b = new LlamaServerBackend
        {
            Name = "always-fails",
            Port = 8080,
            SkipRealProcess = false
        };
        _backends.Add(b);

        var spec = new LlamaSwapModelSpec("always-fails", "", 0, 0, []);
        for (var i = 0; i < 3; i++)
        {
            await b.StartAsync(spec, CancellationToken.None);
            Assert.Equal(BackendState.Faulted, b.State);
        }
    }

    [Fact]
    public void Supervision_RestartLimit_TransitionsToFaulted()
    {
        var fsm = new ModelBackendFsm();
        fsm.TryTransition(BackendState.Starting);
        fsm.TryTransition(BackendState.Faulted, "crash");

        for (var i = 0; i < 5; i++)
        {
            fsm.ForceReset();
            fsm.TryTransition(BackendState.Starting);
            fsm.TryTransition(BackendState.Faulted, $"crash {i + 1}");
        }

        Assert.Equal(BackendState.Faulted, fsm.Current);
        Assert.Contains("crash 5", fsm.LastError);
    }

    [Fact]
    public async Task StopAsync_FromFaultedState_ResetsToIdle()
    {
        var b = new LlamaServerBackend
        {
            Name = "stop-faulted",
            Port = 8080,
            SkipRealProcess = false
        };
        _backends.Add(b);
        await b.StartAsync(new LlamaSwapModelSpec("stop-faulted", "", 0, 0, []), CancellationToken.None);
        Assert.Equal(BackendState.Faulted, b.State);

        await b.StopAsync(CancellationToken.None);
        Assert.Equal(BackendState.Idle, b.State);
    }

    public void Dispose()
    {
        foreach (var b in _backends)
            b.Dispose();
    }
}
