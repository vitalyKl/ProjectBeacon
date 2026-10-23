namespace ProjectBeacon.Application.Tests;

using Application.Chat;

public sealed class ChatEventSignalTests
{
    [Fact]
    public async Task WaitAsync_UnknownSession_TimesOut()
    {
        var signal = new ChatEventSignal();
        var started = DateTime.UtcNow;
        var changed = await signal.WaitAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(200));
        Assert.False(changed);
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds >= 180);
    }

    [Fact]
    public async Task WaitAsync_ReturnsTrue_WhenSignaledDuringWait()
    {
        var signal = new ChatEventSignal();
        var id = Guid.NewGuid();
        var wait = signal.WaitAsync(id, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        var started = DateTime.UtcNow;
        signal.Signal(id);
        var changed = await wait;
        Assert.True(changed);
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds < 2000);
    }

    [Fact]
    public async Task WaitAsync_EdgeSemantics_SignalBeforeWaitNotObserved()
    {
        var signal = new ChatEventSignal();
        var id = Guid.NewGuid();
        signal.Signal(id);
        Assert.Equal(1, signal.GetVersion(id));
        var changed = await signal.WaitAsync(id, TimeSpan.FromMilliseconds(150));
        Assert.False(changed);
        Assert.Equal(1, signal.GetVersion(id));
    }

    [Fact]
    public void Signal_VersionIncrements()
    {
        var signal = new ChatEventSignal();
        var id = Guid.NewGuid();
        signal.Signal(id);
        signal.Signal(id);
        Assert.Equal(2, signal.GetVersion(id));
        Assert.Equal(0, signal.GetVersion(Guid.NewGuid()));
    }

    [Fact]
    public async Task WaitAsync_CancellationPropagates()
    {
        var signal = new ChatEventSignal();
        using var cts = new CancellationTokenSource();
        var wait = signal.WaitAsync(Guid.NewGuid(), TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task MultipleWaiters_AllWakeOnSignal()
    {
        var signal = new ChatEventSignal();
        var id = Guid.NewGuid();
        var first = signal.WaitAsync(id, TimeSpan.FromSeconds(5));
        var second = signal.WaitAsync(id, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        signal.Signal(id);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, r => Assert.True(r));
    }
}