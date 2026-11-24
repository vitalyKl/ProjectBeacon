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
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-desired", user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);

        var saved = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, user.Id, WorkstationCommandKind.SaveWorkstation, """{"modelsRoot":"D:\\\\models","revision":0}""", null)), TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.Error);

        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        Assert.Equal(1, beat.Value!.DesiredRevision);
        Assert.Equal(0, beat.Value.AppliedRevision);

        var again = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Value!.AppliedRevision);

        var listed = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)), TestContext.Current.CancellationToken);
        var reconcile = listed.Value!.Single(c => c.Kind == WorkstationCommandKind.ReconcileDesired);
        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        while (claimed.Value is { Kind: not WorkstationCommandKind.ReconcileDesired })
        {
            await new CompleteCommandHandler(Factory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value.Id, deviceId, true, "{}", null)), TestContext.Current.CancellationToken);
            claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
                new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        }
        Assert.NotNull(claimed.Value);
        var done = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value!.Id, deviceId, true, reconcile.PayloadJson, null)), TestContext.Current.CancellationToken);
        Assert.True(done.Success, done.Error);

        var after = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        Assert.Equal(1, after.Value!.AppliedRevision);
        var listedAfter = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)), TestContext.Current.CancellationToken);
        Assert.Equal(1, listedAfter.Value!.Count(c => c.Kind == WorkstationCommandKind.ReconcileDesired));
    }

    [Fact]
    public async Task DeviceProjectRuntimeDiscovery_IsDeviceScopedAndOnlyTreatsGitLandMutationsAsBusy()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        using (TenantScope.EnterUnscoped())
        {
            var firstDevice = await _db.DaemonDevices.IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == deviceId, TestContext.Current.CancellationToken);
            firstDevice.Heartbeat("""{"capabilities":["git-sync-v1","git-autosync-v1"]}""", "{}");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var secondDevice = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("second", "fp-second-" + Guid.NewGuid().ToString("N"), user.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(secondDevice.Success, secondDevice.Error);

        using (TenantScope.EnterUnscoped())
        {
            _db.ProjectRuntimes.AddRange(
                ProjectRuntime.Create(project.Id, deviceId, "/workspace/first"),
                ProjectRuntime.Create(project.Id, secondDevice.Value!.Id, "/workspace/second"));
            _db.WorkstationCommands.AddRange(
                WorkstationCommand.Create(deviceId, WorkstationCommandKind.GitSync,
                    """{"projectId":"project","mode":"Status"}""", project.Id),
                WorkstationCommand.Create(deviceId, WorkstationCommandKind.GitLand,
                    """{"action":"Preview"}""", project.Id),
                WorkstationCommand.Create(deviceId, WorkstationCommandKind.GitLand,
                    """{"action":"Execute"}""", project.Id),
                WorkstationCommand.Create(deviceId, WorkstationCommandKind.ApplyProposalPatch,
                    "{}", project.Id));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        IReadOnlyList<DeviceProjectRuntimeDto> runtimes;
        using (TenantScope.EnterUnscoped())
            runtimes = await new ListDeviceProjectRuntimesHandler(Factory())
                .HandleAsync(deviceId, TestContext.Current.CancellationToken);

        var runtime = Assert.Single(runtimes);
        Assert.Equal(project.Id, runtime.ProjectId);
        Assert.Equal("/workspace/first", runtime.LocalRoot);
        Assert.True(runtime.ManualGitSyncActive);
        Assert.True(runtime.GitLandActive);
        Assert.True(runtime.CheckoutMutationActive);
    }

    [Fact]
    public async Task Heartbeat_DoesNotRequeueRetiredAgentConfig_WhenLegacyConfigRevisionAhead()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, @"A:\work\app")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters().SingleAsync(r => r.DeviceId == deviceId, TestContext.Current.CancellationToken);
            runtime.BumpConfig();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")), TestContext.Current.CancellationToken);
        var listed = await new ListCommandsHandler(Factory()).HandleAsync(
            new ListCommandsCommand(new ListCommandsRequest(deviceId, user.Id, 20)), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(listed.Value!, c => c.Kind == WorkstationCommandKind.RetiredAgentConfigApply);
    }
}
