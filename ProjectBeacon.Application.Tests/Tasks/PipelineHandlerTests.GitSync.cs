namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Devices;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task GetPipeline_RequiresFirstGitSyncBeforeSnapshotOnCapableWorkstation()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync preflight");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var result = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(device.Id, result.Value!.Preflight!.PipelineDeviceId);
        Assert.True(result.Value.Preflight.PipelineGitSyncSupported);
        Assert.True(result.Value.Preflight.RepositorySyncBlocked);
        Assert.False(result.Value.Preflight.RepositorySyncInProgress);
        Assert.Equal("PipelineRepositorySyncRequired", result.Value.StartBlockedReason);
    }

    [Fact]
    public async Task StartPipeline_DoesNotChangeTaskStageUntilGitSyncHasSucceeded()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync required before start");
        await SeedGitSyncRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var result = await new StartPipelineHandler(factory, new FakeSpawner(Guid.NewGuid()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Null(persisted.PipelineStage);
            Assert.Empty(await _db.PipelineSessions.AsNoTracking()
                .Where(session => session.TaskId == task.Id)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LaunchSession_RechecksGitSyncForTheWorkstationActuallySelectedForExecution()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync race at launch");
        var user = User.Create("launch-sync", $"launch-sync-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "git-sync workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"agentRuntime":"native"}""");
        var backend = LocalModelBackend.Create(
            "git-sync-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        var runtime = ProjectRuntime.Create(projectId, device.Id, "/workspace/git-sync");
        var session = PipelineSession.Create(
            task.Id, projectId, PipelineRole.Planner, "Planner prompt", modelBackendId: backend.Id);

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.LocalModelBackends.Add(backend);
            _db.ProjectRuntimes.Add(runtime);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var result = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(session.Id)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(item => item.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Ready, persisted.Status);
            Assert.Empty(await _db.WorkstationCommands.AsNoTracking()
                .Where(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task GetPipeline_AllowsCapableWorkstationAfterVerifiedUpToDateSync()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync completed");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var sha = new string('a', 40);
        var syncResult = new GitSyncResultDto(
            "main", "origin/main", 0, 0, true, false, true, false,
            sha, sha, GitSyncOutcome.UpToDate, null, null);
        command.Succeed(JsonSerializer.Serialize(
            syncResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var result = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Preflight!.PipelineGitSyncSupported);
        Assert.False(result.Value.Preflight.RepositorySyncBlocked);
        Assert.Equal(0, result.Value.Preflight.LastKnownRepositoryBehind);
    }

    [Fact]
    public async Task GetPipeline_DoesNotRequireGitSyncFromLegacyWorkstationWithoutCapability()
    {
        var (projectId, task) = await SeedTaskAsync("Legacy workstation");
        var user = User.Create("legacy-sync", $"legacy-sync-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "legacy workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat("{}", """{"agentRuntime":"native"}""");
        var runtime = ProjectRuntime.Create(projectId, device.Id, "/workspace/legacy-sync");
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.ProjectRuntimes.Add(runtime);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var result = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.PipelineGitSyncSupported);
        Assert.False(result.Value.Preflight.RepositorySyncBlocked);
    }

    private async Task<DaemonDevice> SeedGitSyncRuntimeAsync(Guid projectId)
    {
        var user = User.Create("git-sync", $"git-sync-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "git-sync workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"agentRuntime":"native"}""");
        var runtime = ProjectRuntime.Create(projectId, device.Id, "/workspace/git-sync");
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.ProjectRuntimes.Add(runtime);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        return device;
    }
}
