namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Tasks;
using Application.Devices;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Tasks;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task StartPipeline_PinsTaskAndLaterLaunchIgnoresNewerWorkstation()
    {
        var (projectId, task) = await SeedTaskAsync("Pinned task workspace");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var planner = await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(
                new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);

        Assert.True(planner.Success, planner.Error);

        Guid originalDeviceId;
        using (TenantScope.EnterUnscoped())
        {
            originalDeviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .Select(runtime => runtime.DeviceId)
                .SingleAsync(TestContext.Current.CancellationToken);
            var persistedTask = await _db.Tasks.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(originalDeviceId, persistedTask.PipelineDeviceId);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        var newerDevice = await AddNativeWorkstationAsync(projectId, "Newer workstation");

        var launched = await new LaunchSessionHandler(factory)
            .HandleAsync(
                new LaunchSessionCommand(new LaunchSessionRequest(planner.Value!.Id)),
                TestContext.Current.CancellationToken);

        Assert.True(launched.Success, launched.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persistedTask = await _db.Tasks.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(originalDeviceId, persistedTask.PipelineDeviceId);
            Assert.NotEqual(originalDeviceId, newerDevice.Id);

            var runCommand = await _db.WorkstationCommands.IgnoreQueryFilters()
                .SingleAsync(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession
                    && command.Status == WorkstationCommandStatus.Pending,
                    TestContext.Current.CancellationToken);
            Assert.Equal(originalDeviceId, runCommand.DeviceId);
        }
    }

    [Fact]
    public async Task LaunchSession_InfersSingleLegacyWorkstationInsteadOfSelectingLatestHeartbeat()
    {
        var (projectId, task) = await SeedTaskAsync("Legacy task workspace");
        Guid historicalDeviceId;
        Guid plannerBackendId;
        using (TenantScope.EnterUnscoped())
        {
            historicalDeviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .Select(runtime => runtime.DeviceId)
                .SingleAsync(TestContext.Current.CancellationToken);
            plannerBackendId = await _db.RoleBindings.IgnoreQueryFilters()
                .Where(binding => binding.ProjectId == projectId && binding.Role == PipelineRole.Planner)
                .Select(binding => binding.ModelBackendId)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        var newerDevice = await AddNativeWorkstationAsync(projectId, "Newer legacy workstation");
        var priorRun = WorkstationCommand.Create(
            historicalDeviceId,
            WorkstationCommandKind.RunPipelineSession,
            JsonSerializer.Serialize(new { projectId, taskId = task.Id }),
            projectId);
        priorRun.Claim(TimeSpan.FromMinutes(5));
        var session = PipelineSession.Create(
            task.Id,
            projectId,
            PipelineRole.Planner,
            "Legacy Planner prompt",
            modelBackendId: plannerBackendId);

        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(priorRun);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var launched = await new LaunchSessionHandler(factory)
            .HandleAsync(
                new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
                TestContext.Current.CancellationToken);

        Assert.True(launched.Success, launched.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persistedTask = await _db.Tasks.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(historicalDeviceId, persistedTask.PipelineDeviceId);
            Assert.NotEqual(historicalDeviceId, newerDevice.Id);

            var newlyQueued = await _db.WorkstationCommands.IgnoreQueryFilters()
                .Where(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession
                    && command.Status == WorkstationCommandStatus.Pending)
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(historicalDeviceId, newlyQueued.DeviceId);
        }
    }

    [Fact]
    public async Task LaunchSession_IgnoresCommandRejectedBeforeWorkstationClaimWhenInferringLegacyPin()
    {
        var (projectId, task) = await SeedTaskAsync("Legacy task with pre-claim failure");
        Guid originalDeviceId;
        Guid plannerBackendId;
        using (TenantScope.EnterUnscoped())
        {
            originalDeviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .Select(runtime => runtime.DeviceId)
                .SingleAsync(TestContext.Current.CancellationToken);
            plannerBackendId = await _db.RoleBindings.IgnoreQueryFilters()
                .Where(binding => binding.ProjectId == projectId && binding.Role == PipelineRole.Planner)
                .Select(binding => binding.ModelBackendId)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        var currentNativeDevice = await AddNativeWorkstationAsync(projectId, "Current native workstation");
        var rejectedBeforeClaim = WorkstationCommand.Create(
            originalDeviceId,
            WorkstationCommandKind.RunPipelineSession,
            JsonSerializer.Serialize(new { projectId, taskId = task.Id }),
            projectId);
        rejectedBeforeClaim.Fail(CommandSandbox.RuntimeRequired);
        var session = PipelineSession.Create(
            task.Id,
            projectId,
            PipelineRole.Planner,
            "Legacy Planner after pre-claim rejection",
            modelBackendId: plannerBackendId);

        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(rejectedBeforeClaim);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var launched = await new LaunchSessionHandler(factory)
            .HandleAsync(
                new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
                TestContext.Current.CancellationToken);

        Assert.True(launched.Success, launched.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persistedTask = await _db.Tasks.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(currentNativeDevice.Id, persistedTask.PipelineDeviceId);
            Assert.NotEqual(originalDeviceId, persistedTask.PipelineDeviceId);

            var queued = await _db.WorkstationCommands.IgnoreQueryFilters()
                .Where(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession
                    && command.Status == WorkstationCommandStatus.Pending)
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(currentNativeDevice.Id, queued.DeviceId);
        }
    }

    [Fact]
    public async Task LaunchSession_RejectsLegacyTaskWithRunsOnMultipleWorkstations()
    {
        var (projectId, task) = await SeedTaskAsync("Ambiguous legacy task workspace");
        Guid firstDeviceId;
        Guid plannerBackendId;
        using (TenantScope.EnterUnscoped())
        {
            firstDeviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .Select(runtime => runtime.DeviceId)
                .SingleAsync(TestContext.Current.CancellationToken);
            plannerBackendId = await _db.RoleBindings.IgnoreQueryFilters()
                .Where(binding => binding.ProjectId == projectId && binding.Role == PipelineRole.Planner)
                .Select(binding => binding.ModelBackendId)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var secondDevice = await AddNativeWorkstationAsync(projectId, "Second historical workstation");
        var firstRun = WorkstationCommand.Create(
            firstDeviceId,
            WorkstationCommandKind.RunPipelineSession,
            JsonSerializer.Serialize(new { projectId, taskId = task.Id }),
            projectId);
        firstRun.Claim(TimeSpan.FromMinutes(5));
        var secondRun = WorkstationCommand.Create(
            secondDevice.Id,
            WorkstationCommandKind.RunPipelineSession,
            JsonSerializer.Serialize(new { projectId, taskId = task.Id }),
            projectId);
        secondRun.Claim(TimeSpan.FromMinutes(5));
        var session = PipelineSession.Create(
            task.Id,
            projectId,
            PipelineRole.Planner,
            "Ambiguous legacy Planner prompt",
            modelBackendId: plannerBackendId);

        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.AddRange(firstRun, secondRun);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var launch = await new LaunchSessionHandler(factory)
            .HandleAsync(
                new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
                TestContext.Current.CancellationToken);

        Assert.False(launch.Success);
        Assert.Contains("multiple workstations", launch.Error, StringComparison.OrdinalIgnoreCase);

        var pipeline = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(pipeline.Success, pipeline.Error);
        Assert.False(pipeline.Value!.CanStart);
        Assert.Equal("PipelineWorkstationHistoryAmbiguous",
            pipeline.Value.Preflight!.PipelineWorkstationBlockedReason);
        using (TenantScope.EnterUnscoped())
        {
            var persistedTask = await _db.Tasks.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Null(persistedTask.PipelineDeviceId);
            Assert.Equal(2, await _db.WorkstationCommands.IgnoreQueryFilters()
                .CountAsync(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession,
                    TestContext.Current.CancellationToken));
        }
    }

    private async Task<DaemonDevice> AddNativeWorkstationAsync(Guid projectId, string name)
    {
        var user = User.Create(
            "pipeline-workstation-" + Guid.NewGuid().ToString("N"),
            $"pipeline-workstation-{Guid.NewGuid():N}@beacon.local",
            "hash");
        var device = DaemonDevice.Create(
            name,
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat("{}", """{"agentRuntime":"native"}""");

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(projectId, device.Id, Path.GetTempPath()));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return device;
    }
}
