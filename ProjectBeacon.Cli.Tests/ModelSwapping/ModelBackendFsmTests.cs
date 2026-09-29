namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;

public sealed class ModelBackendFsmTests
{
    [Theory]
    [InlineData(BackendState.Idle, BackendState.Starting, true)]
    [InlineData(BackendState.Starting, BackendState.Ready, true)]
    [InlineData(BackendState.Starting, BackendState.Faulted, true)]
    [InlineData(BackendState.Ready, BackendState.Stopping, true)]
    [InlineData(BackendState.Ready, BackendState.Faulted, true)]
    [InlineData(BackendState.Stopping, BackendState.Idle, true)]
    [InlineData(BackendState.Faulted, BackendState.Starting, true)]
    [InlineData(BackendState.Faulted, BackendState.Idle, true)]
    public void ValidTransitions_Accepted(BackendState from, BackendState to, bool expected)
    {
        var fsm = new ModelBackendFsm();
        ForceTo(fsm, from);
        var ok = fsm.TryTransition(to);
        Assert.Equal(expected, ok);
        Assert.Equal(to, fsm.Current);
    }

    [Theory]
    [InlineData(BackendState.Idle, BackendState.Ready)]
    [InlineData(BackendState.Idle, BackendState.Faulted)]
    [InlineData(BackendState.Idle, BackendState.Stopping)]
    [InlineData(BackendState.Starting, BackendState.Idle)]
    [InlineData(BackendState.Starting, BackendState.Stopping)]
    [InlineData(BackendState.Ready, BackendState.Starting)]
    [InlineData(BackendState.Ready, BackendState.Idle)]
    [InlineData(BackendState.Stopping, BackendState.Starting)]
    [InlineData(BackendState.Stopping, BackendState.Ready)]
    [InlineData(BackendState.Stopping, BackendState.Faulted)]
    [InlineData(BackendState.Faulted, BackendState.Ready)]
    [InlineData(BackendState.Faulted, BackendState.Stopping)]
    public void InvalidTransitions_Rejected(BackendState from, BackendState to)
    {
        var fsm = new ModelBackendFsm();
        ForceTo(fsm, from);
        var ok = fsm.TryTransition(to);
        Assert.False(ok);
        Assert.Equal(from, fsm.Current);
    }

    [Fact]
    public void FaultedTransition_RecordsError()
    {
        var fsm = new ModelBackendFsm();
        fsm.TryTransition(BackendState.Starting);
        var ok = fsm.TryTransition(BackendState.Faulted, "process crashed");
        Assert.True(ok);
        Assert.Equal("process crashed", fsm.LastError);
    }

    [Fact]
    public void ForceReset_ClearsStateAndError()
    {
        var fsm = new ModelBackendFsm();
        fsm.TryTransition(BackendState.Starting);
        fsm.TryTransition(BackendState.Faulted, "boom");
        fsm.ForceReset();
        Assert.Equal(BackendState.Idle, fsm.Current);
        Assert.Null(fsm.LastError);
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        var fsm = new ModelBackendFsm();
        Assert.Equal(BackendState.Idle, fsm.Current);
        Assert.Null(fsm.LastError);
    }

    private static void ForceTo(ModelBackendFsm fsm, BackendState target)
    {
        if (fsm.Current == target) return;
        fsm.ForceReset();
        foreach (var step in PathTo(target))
            fsm.TryTransition(step);
    }

    private static IEnumerable<BackendState> PathTo(BackendState target) => target switch
    {
        BackendState.Idle => [],
        BackendState.Starting => [BackendState.Starting],
        BackendState.Ready => [BackendState.Starting, BackendState.Ready],
        BackendState.Stopping => [BackendState.Starting, BackendState.Ready, BackendState.Stopping],
        BackendState.Faulted => [BackendState.Starting, BackendState.Faulted],
        _ => []
    };
}
