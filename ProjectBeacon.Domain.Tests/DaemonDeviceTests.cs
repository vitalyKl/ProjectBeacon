namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Projects;
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
}

public sealed class WorkstationCommandTests
{
    [Fact]
    public void Claim_Succeed_Fail()
    {
        var command = WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.ListDir, "{\"path\":\"C:\\\\\"}");
        Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
        command.Claim();
        Assert.Equal(WorkstationCommandStatus.Running, command.Status);
        command.Succeed("{\"entries\":[]}");
        Assert.Equal(WorkstationCommandStatus.Succeeded, command.Status);
        Assert.Throws<InvalidOperationException>(() => command.Fail("nope"));
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
