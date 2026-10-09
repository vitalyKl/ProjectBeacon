namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed partial class DeviceHandlerTests
{
    [Fact]
    public async Task Create_ThenList_HidesRawTokenOnList()
    {
        var user = await SeedUserAsync();
        var create = new CreateDeviceHandler(Factory());
        var created = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-1", user.Id)));
        Assert.True(created.Success, created.Error);
        Assert.StartsWith("bcd_", created.Value!.Token);
        Assert.Equal(user.Id, created.Value.UserId);

        var listed = await new ListDevicesHandler(Factory()).HandleAsync(new ListDevicesCommand(new ListDevicesRequest(user.Id)));
        Assert.True(listed.Success);
        Assert.Single(listed.Value!);
        Assert.Null(listed.Value![0].Token);
        Assert.False(listed.Value[0].Online);
    }

    [Fact]
    public async Task Create_SameFingerprint_RotatesToken()
    {
        var user = await SeedUserAsync();
        var create = new CreateDeviceHandler(Factory());
        var first = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("a", "fp", user.Id)));
        var second = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("b", "fp", user.Id)));
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.NotEqual(first.Value.Token, second.Value.Token);
        Assert.Equal("b", second.Value.Name);
    }

    [Fact]
    public async Task Enqueue_RequiresOnlineDevice()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(created.Value!.Id, user.Id, WorkstationCommandKind.Probe, null, null)));
        Assert.False(queued.Success);
        Assert.Equal("Device is not connected.", queued.Error);
    }

    [Fact]
    public async Task Heartbeat_Claim_Complete_RoundTrip()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        var deviceId = created.Value!.Id;

        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{\"git\":\"git\"}", "{}")));
        Assert.True(beat.Success, beat.Error);
        Assert.True(beat.Value!.Online);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(deviceId, user.Id, WorkstationCommandKind.ListDir, "{\"path\":\"\"}", null)));
        Assert.True(queued.Success, queued.Error);

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        Assert.True(claimed.Success, claimed.Error);
        Assert.NotNull(claimed.Value);
        Assert.Equal(WorkstationCommandStatus.Running, claimed.Value!.Status);

        var empty = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        Assert.Null(empty.Value);

        var done = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value.Id, deviceId, true, "{\"entries\":[]}", null)));
        Assert.True(done.Success, done.Error);
        Assert.Equal(WorkstationCommandStatus.Succeeded, done.Value!.Status);

        var got = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(claimed.Value.Id, user.Id)));
        Assert.Equal("{\"entries\":[]}", got.Value!.ResultJson);
    }

    [Fact]
    public async Task AttachRuntime_PerDeviceRoot()
    {
        var (user, project) = await SeedMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value!.Id, user.Id, @"A:\work\app")));
        Assert.True(attached.Success, attached.Error);
        Assert.Equal(@"A:\work\app", attached.Value!.LocalRoot);

        var again = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value.Id, user.Id, @"A:\work\other")));
        Assert.Equal(attached.Value.Id, again.Value!.Id);
        Assert.Equal(@"A:\work\other", again.Value.LocalRoot);

        var listed = await new ListRuntimesHandler(Factory()).HandleAsync(
            new ListRuntimesCommand(new ListRuntimesRequest(project.Id, user.Id)));
        Assert.Single(listed.Value!);

        var detached = await new DetachRuntimeHandler(Factory()).HandleAsync(
            new DetachRuntimeCommand(new DetachRuntimeRequest(listed.Value![0].Id, user.Id)));
        Assert.True(detached.Success, detached.Error);
    }

    [Fact]
    public async Task Heartbeat_RecordsHostSample()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id,
                """{"hostLoad":{"cpuPercent":40,"ramUsedBytes":1,"ramTotalBytes":2,"sampledAt":"2026-09-20T12:00:00Z"}}""", "{}")));
        Assert.True(beat.Success, beat.Error);
        var samples = await new ListHostSamplesHandler(Factory()).HandleAsync(
            new ListHostSamplesCommand(new ListHostSamplesRequest(user.Id, created.Value.Id)));
        Assert.True(samples.Success, samples.Error);
        Assert.Single(samples.Value!);
        Assert.Equal(40, samples.Value![0].CpuPercent);
    }

    [Fact]
    public async Task Revoke_HidesFromHeartbeat()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        await new RevokeDeviceHandler(Factory()).HandleAsync(
            new RevokeDeviceCommand(new RevokeDeviceRequest(created.Value!.Id, user.Id)));
        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value.Id, null, null)));
        Assert.False(beat.Success);
    }

    [Fact]
    public async Task Revoke_HidesFromList()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)));
        await new RevokeDeviceHandler(Factory()).HandleAsync(
            new RevokeDeviceCommand(new RevokeDeviceRequest(created.Value!.Id, user.Id)));
        var listed = await new ListDevicesHandler(Factory()).HandleAsync(
            new ListDevicesCommand(new ListDevicesRequest(user.Id)));
        Assert.True(listed.Success);
        Assert.Empty(listed.Value!);
    }
}
