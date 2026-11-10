namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Operations;

public sealed class PlatformOperationsContractTests
{
    [Fact]
    public void HeartbeatWrite_AcceptsBoundedUtcMetadata()
    {
        var started = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        var heartbeat = new PlatformServiceHeartbeatWrite(
            PlatformServiceKind.Web,
            "a1b2c3d4e5f6",
            "0.7.5.4+build.12",
            "Production",
            started,
            started.AddSeconds(30),
            PlatformServiceHealth.Healthy);

        heartbeat.Validate();
    }

    [Theory]
    [InlineData("instance id")]
    [InlineData("instance@host")]
    [InlineData("")]
    public void HeartbeatWrite_RejectsUnsafeInstanceIdentifiers(string instanceId)
    {
        var now = DateTimeOffset.Parse("2026-11-10T10:00:00Z");
        var heartbeat = new PlatformServiceHeartbeatWrite(
            PlatformServiceKind.Web, instanceId, "0.7.5.4", "Production",
            now, now, PlatformServiceHealth.Healthy);

        Assert.Throws<ArgumentException>(heartbeat.Validate);
    }

    [Fact]
    public void HeartbeatWrite_RejectsNonUtcTimestamps()
    {
        var local = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(2));
        var heartbeat = new PlatformServiceHeartbeatWrite(
            PlatformServiceKind.Api, "api-01", "0.7.5.4", "Production",
            local, local, PlatformServiceHealth.Healthy);

        Assert.Throws<ArgumentException>(heartbeat.Validate);
    }

    [Fact]
    public void HeartbeatWrite_RejectsHeartbeatBeforeProcessStart()
    {
        var started = DateTimeOffset.Parse("2026-11-10T10:00:30Z");
        var heartbeat = new PlatformServiceHeartbeatWrite(
            PlatformServiceKind.Worker, "worker-01", "0.7.5.4", "Production",
            started, started.AddSeconds(-1), PlatformServiceHealth.Healthy);

        Assert.Throws<ArgumentException>(heartbeat.Validate);
    }
}
