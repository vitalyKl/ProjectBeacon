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
    public async Task Heartbeat_ReconcilesDesired_Once_AndCompleteMarksApplied()
    {
        var user = await SeedUserAsync("ws");
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-desired", user.Id)));
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));

        var saved = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"modelsRoot":"D:\\\\models"}""", null)));
        Assert.True(saved.Success, saved.Error);

        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        Assert.Equal(1, beat.Value!.DesiredRevision);
        Assert.Equal(0, beat.Value.AppliedRevision);

        var again = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        Assert.Equal(0, again.Value!.AppliedRevision);

        var listed = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)));
        var reconcile = listed.Value!.Single(c => c.Kind == WorkstationCommandKind.ReconcileDesired);
        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        while (claimed.Value is { Kind: not WorkstationCommandKind.ReconcileDesired })
        {
            await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value.Id, deviceId, true, "{}", null)));
            claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
                new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        }
        Assert.NotNull(claimed.Value);
        var done = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value!.Id, deviceId, true, reconcile.PayloadJson, null)));
        Assert.True(done.Success, done.Error);

        var after = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        Assert.Equal(1, after.Value!.AppliedRevision);
        var listedAfter = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)));
        Assert.Equal(1, listedAfter.Value!.Count(c => c.Kind == WorkstationCommandKind.ReconcileDesired));
    }

    [Fact]
    public async Task Heartbeat_RequeuesApplyOpencode_WhenConfigRevisionAhead()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, @"A:\work\app")));
        Assert.True(attached.Success, attached.Error);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters().SingleAsync(r => r.DeviceId == deviceId);
            runtime.BumpConfig();
            await _db.SaveChangesAsync();
        }

        var listed = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)));
        Assert.DoesNotContain(listed.Value!, c => c.Kind == WorkstationCommandKind.ApplyOpencode);

        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        listed = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)));
        var apply = Assert.Single(listed.Value!, c => c.Kind == WorkstationCommandKind.ApplyOpencode);
        Assert.Equal(project.Id, apply.ProjectId);

        var done = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(apply.Id, deviceId, true, apply.PayloadJson, null)));
        Assert.True(done.Success, done.Error);

        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")));
        var after = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)));
        Assert.Equal(1, after.Value!.Count(c => c.Kind == WorkstationCommandKind.ApplyOpencode));
    }
}
