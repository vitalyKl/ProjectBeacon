namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Enums;

public sealed class WorkstationCommandTests
{
    [Fact]
    public void PendingSinceTracksEachQueuePeriod()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.GitSync);
        Assert.NotNull(command.PendingSince);
        var firstPendingSince = command.PendingSince;

        command.Claim(TimeSpan.FromMinutes(5), "daemon-1");
        Assert.Null(command.PendingSince);

        command.Requeue();
        Assert.NotNull(command.PendingSince);
        Assert.True(command.PendingSince >= firstPendingSince);

        command.Claim(TimeSpan.FromMinutes(5), "daemon-2");
        command.Succeed("{}");
        Assert.Null(command.PendingSince);
    }

    [Fact]
    public void CancelFromPendingMarksCommandTerminal()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.RunPipelineSession);

        command.Cancel();

        Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status);
        Assert.NotNull(command.CompletedAt);
        Assert.Null(command.LeaseExpiresAt);
    }

    [Fact]
    public void CancelFromRunningClearsExecutionLease()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.RunPipelineSession);
        command.Claim(TimeSpan.FromMinutes(5), "daemon-1");

        Assert.NotNull(command.LeaseExpiresAt);

        command.Cancel();

        Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status);
        Assert.NotNull(command.CompletedAt);
        Assert.Null(command.LeaseExpiresAt);
    }

    [Fact]
    public void CancelFromTerminalStateThrows()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.RunPipelineSession);
        command.Claim(TimeSpan.FromMinutes(5));
        command.Succeed("{}");

        Assert.Throws<InvalidOperationException>(() => command.Cancel());
        Assert.Equal(WorkstationCommandStatus.Succeeded, command.Status);
    }
}
