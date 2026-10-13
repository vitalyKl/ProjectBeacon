namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Enums;

public sealed class DaemonDeviceTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var userId = Guid.NewGuid();
        var device = DaemonDevice.Create("laptop", userId, "fp-1", "hash", "bcd_xxxx");

        Assert.NotEqual(Guid.Empty, device.Id);
        Assert.Equal("laptop", device.Name);
        Assert.Equal(userId, device.UserId);
        Assert.Equal("fp-1", device.Fingerprint);
        Assert.False(device.IsRevoked);
        Assert.False(device.IsOnline(DateTime.UtcNow));
    }

    [Fact]
    public void Heartbeat_MakesOnline()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h", "bcd_xx");
        device.Heartbeat("{\"git\":\"/bin/git\"}", "{\"modelsRoot\":\"m\"}");
        Assert.True(device.IsOnline(DateTime.UtcNow));
        Assert.Contains("git", device.ProbeJson);
    }

    [Fact]
    public void Heartbeat_AfterWindow_IsOffline()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h", "bcd_xx");
        device.Heartbeat();
        Assert.False(device.IsOnline(DateTime.UtcNow.AddSeconds(DaemonDevice.OnlineWindowSeconds + 1)));
    }

    [Fact]
    public void Revoke_BlocksHeartbeat()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h", "bcd_xx");
        device.Revoke();
        Assert.True(device.IsRevoked);
        Assert.Throws<InvalidOperationException>(() => device.Heartbeat());
    }

    [Fact]
    public void RotateToken_UpdatesHash()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h1", "bcd_aa");
        device.RotateToken("h2", "bcd_bb");
        Assert.Equal("h2", device.TokenHash);
        Assert.Equal("bcd_bb", device.TokenPrefix);
    }

    [Fact]
    public void MarkApplied_DoesNotPassDesired()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h", "bcd_xx");
        device.BumpDesired();
        device.BumpDesired();
        device.MarkApplied(5);
        Assert.Equal(2, device.AppliedRevision);
        device.BumpDesired();
        Assert.Equal(2, device.AppliedRevision);
        Assert.Equal(3, device.DesiredRevision);
    }
}

public sealed class WorkstationCommandTests
{
    [Fact]
    public void Claim_Succeed_Fail()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.ListDir, "{\"path\":\"C:\\\\\"}");
        Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
        command.Claim(TimeSpan.FromMinutes(15));
        Assert.Equal(WorkstationCommandStatus.Running, command.Status);
        command.Succeed("{\"entries\":[]}");
        Assert.Equal(WorkstationCommandStatus.Succeeded, command.Status);
        Assert.Null(command.LeaseExpiresAt);
        Assert.Throws<InvalidOperationException>(() => command.Fail("nope"));
    }

    [Fact]
    public void Claim_GrantsLeaseAndRecordsInstance()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.ListDir);
        var lease = TimeSpan.FromMinutes(15);
        command.Claim(lease, " inst-1 ");
        Assert.NotNull(command.LeaseExpiresAt);
        Assert.True(command.LeaseExpiresAt > DateTime.UtcNow);
        Assert.Equal(command.StartedAt + lease, command.LeaseExpiresAt);
        Assert.Equal("inst-1", command.ClaimInstanceId);
    }

    [Fact]
    public void Claim_BlankInstanceIsUnknown()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe);
        command.Claim(TimeSpan.FromMinutes(15), "   ");
        Assert.Null(command.ClaimInstanceId);
    }

    [Fact]
    public void Claim_RequiresPositiveLease()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.Claim(TimeSpan.Zero));
    }

    [Fact]
    public void Requeue_ReturnsPendingIncrementsAttemptsAndClearsLease()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.ListDir);
        command.Claim(TimeSpan.FromMinutes(15), "inst-1");
        command.Requeue();
        Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
        Assert.Equal(1, command.Attempts);
        Assert.Null(command.LeaseExpiresAt);
        Assert.Null(command.StartedAt);
        command.Claim(TimeSpan.FromMinutes(15), "inst-2");
        command.Requeue();
        Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
        Assert.Equal(2, command.Attempts);
    }

    [Fact]
    public void Requeue_RequiresRunning()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe);
        Assert.Throws<InvalidOperationException>(() => command.Requeue());
    }

    [Fact]
    public void Fail_ClearsLease()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe);
        command.Claim(TimeSpan.FromMinutes(15));
        command.Fail("gone");
        Assert.Null(command.LeaseExpiresAt);
        Assert.Equal("gone", command.Error);
    }

    [Fact]
    public void Heartbeat_SetsActiveInstance()
    {
        var device = DaemonDevice.Create("laptop", Guid.NewGuid(), "fp", "h", "bcd_xx");
        device.Heartbeat(instanceId: "a".PadRight(64, 'b'));
        Assert.Equal("a".PadRight(64, 'b'), device.ActiveInstanceId);
        device.Heartbeat();
        Assert.Equal("a".PadRight(64, 'b'), device.ActiveInstanceId);
        Assert.Throws<InvalidOperationException>(() => device.Heartbeat(instanceId: new string('x', 65)));
    }

    [Fact]
    public void Fail_FromPending()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe);
        command.Fail("offline");
        Assert.Equal(WorkstationCommandStatus.Failed, command.Status);
        Assert.Equal("offline", command.Error);
    }
}

public sealed class ProjectRuntimeTests
{
    [Fact]
    public void Create_AndUpdateRoot()
    {
        var runtime = ProjectRuntime.Create(Guid.NewGuid(), Guid.NewGuid(), @"A:\projects\App");
        Assert.NotEqual(Guid.Empty, runtime.Id);
        runtime.SetLocalRoot(@"A:\projects\Other");
        Assert.Equal(@"A:\projects\Other", runtime.LocalRoot);
        Assert.NotNull(runtime.UpdatedAt);
    }
}
