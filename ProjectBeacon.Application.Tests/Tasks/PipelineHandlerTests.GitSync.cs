namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task GetPipeline_DoesNotRequireFirstGitSyncBeforeSnapshot()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync preflight");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var result = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(device.Id, result.Value!.Preflight!.PipelineDeviceId);
        Assert.True(result.Value.Preflight.PipelineGitSyncSupported);
        Assert.False(result.Value.Preflight.RepositorySyncBlocked);
        Assert.False(result.Value.Preflight.RepositorySyncInProgress);
        Assert.Equal(GitSyncRefusalCode.NeverSynced, result.Value.Preflight.LastKnownRepositoryReasonCode);
        Assert.DoesNotContain("Repository", result.Value.StartBlockedReason ?? string.Empty);
    }

    [Fact]
    public async Task GetPipeline_RetainsLastSuccessfulResultAfterNewerFailedAndCancelledAttempts()
    {
        var (projectId, task) = await SeedTaskAsync("Retain previous GitSync result");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var successful = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "Status" }),
            projectId);
        var sha = new string('a', 40);
        successful.Succeed(JsonSerializer.Serialize(
            new GitSyncResultDto(
                "main", "origin/main", 0, 2, true, false, true, false,
                sha, sha, GitSyncOutcome.UpToDate, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        var cancelled = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "Status" }),
            projectId);
        cancelled.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        var failed = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        failed.Fail("Remote fetch failed: network unavailable.");

        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.AddRange(successful, cancelled, failed);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(2, result.Value.Preflight.LastKnownRepositoryBehind);
        Assert.Equal(GitSyncRefusalCode.FetchFailed, result.Value.Preflight.LastKnownRepositoryReasonCode);
        Assert.True(result.Value.CanStart);
    }

    [Fact]
    public async Task StartPipeline_StartsAfterFailedGitSyncAttempt()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync required before start", seedPipelinePrerequisites: false);
        var plannerOwner = User.Create("git-sync-planner", $"git-sync-planner-{Guid.NewGuid():N}@beacon.local", "hash");
        var plannerBackend = LocalModelBackend.Create(
            "git-sync-planner-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            plannerOwner.Id);
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(plannerOwner);
            _db.LocalModelBackends.Add(plannerBackend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, projectId));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var failedAttempt = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        failedAttempt.Fail("Remote fetch failed: network unavailable.");
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(failedAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var result = await new StartPipelineHandler(factory, new FakeSpawner(Guid.NewGuid()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(persisted.PipelineStage);
            Assert.Single(await _db.PipelineSessions.AsNoTracking()
                .Where(session => session.TaskId == task.Id)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task StartPipeline_StartsWithoutPriorGitSyncVerification()
    {
        var (projectId, task) = await SeedTaskAsync("First run without GitSync", seedPipelinePrerequisites: false);
        var plannerOwner = User.Create("first-sync-planner", $"first-sync-planner-{Guid.NewGuid():N}@beacon.local", "hash");
        var plannerBackend = LocalModelBackend.Create(
            "first-sync-planner-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            plannerOwner.Id);
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(plannerOwner);
            _db.LocalModelBackends.Add(plannerBackend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, projectId));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await SeedGitSyncRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var result = await new StartPipelineHandler(factory, new FakeSpawner(Guid.NewGuid()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Planning, persisted.PipelineStage);
            Assert.Single(await _db.PipelineSessions.AsNoTracking()
                .Where(session => session.TaskId == task.Id)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task StartPipeline_StartsWhenRepositoryIsBehindUpstream()
    {
        var (projectId, task) = await SeedTaskAsync("Run with an outdated local repository", seedPipelinePrerequisites: false);
        var plannerOwner = User.Create("behind-planner", $"behind-planner-{Guid.NewGuid():N}@beacon.local", "hash");
        var plannerBackend = LocalModelBackend.Create(
            "behind-planner-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            plannerOwner.Id);
        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(plannerOwner);
            _db.LocalModelBackends.Add(plannerBackend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Planner, plannerBackend.Id, projectId));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "Status" }),
            projectId);
        var sha = new string('a', 40);
        command.Succeed(JsonSerializer.Serialize(
            new GitSyncResultDto(
                "main", "origin/main", 0, 2, true, false, true, false,
                sha, sha, GitSyncOutcome.UpToDate, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var result = await new StartPipelineHandler(factory, new FakeSpawner(Guid.NewGuid()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Planning, persisted.PipelineStage);
        }
    }

    [Fact]
    public async Task StartPipeline_DoesNotEnterPlanningWhenPlannerModelIsNotConfigured()
    {
        var (projectId, task) = await SeedTaskAsync("Missing Planner model", seedPipelinePrerequisites: false);
        var user = User.Create("missing-planner", $"missing-planner-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "legacy-native-workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat("{}", """{"agentRuntime":"native"}""");

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(projectId, device.Id, "/workspace/legacy"));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var result = await new StartPipelineHandler(factory, new FakeSpawner(Guid.NewGuid()))
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("Planner model", result.Error, StringComparison.OrdinalIgnoreCase);
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
    public async Task LaunchSession_DoesNotRequirePriorGitSyncVerification()
    {
        var (projectId, task) = await SeedTaskAsync("GitSync race at launch", seedPipelinePrerequisites: false);
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

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(item => item.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Active, persisted.Status);
            var pipelineCommand = await _db.WorkstationCommands.AsNoTracking()
                .SingleAsync(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession,
                    TestContext.Current.CancellationToken);
            Assert.Equal(device.Id, pipelineCommand.DeviceId);
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
    public async Task GetPipeline_DoesNotBlockSelectedCloneForPendingSyncOnAnotherWorkstation()
    {
        var (projectId, task) = await SeedTaskAsync("Pending sync on a different workstation");
        var selectedDevice = await SeedGitSyncRuntimeAsync(projectId);
        var otherUser = User.Create("other-git-sync", $"other-git-sync-{Guid.NewGuid():N}@beacon.local", "hash");
        var otherDevice = DaemonDevice.Create(
            "external git-sync workstation",
            otherUser.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        otherDevice.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"agentRuntime":"external"}""");
        var success = WorkstationCommand.Create(
            selectedDevice.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var sha = new string('a', 40);
        success.Succeed(JsonSerializer.Serialize(
            new GitSyncResultDto(
                "main", "origin/main", 0, 0, true, false, true, false,
                sha, sha, GitSyncOutcome.UpToDate, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var otherPending = WorkstationCommand.Create(
            otherDevice.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(otherUser);
            _db.DaemonDevices.Add(otherDevice);
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(projectId, otherDevice.Id, "/workspace/other"));
            _db.WorkstationCommands.AddRange(success, otherPending);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(selectedDevice.Id, result.Value!.Preflight!.PipelineDeviceId);
        Assert.False(result.Value.Preflight.RepositorySyncInProgress);
        Assert.False(result.Value.Preflight.RepositorySyncBlocked);
        Assert.True(result.Value.CanStart);
    }

    [Fact]
    public async Task GetPipeline_DoesNotBlockForGitSyncRunningOnAnotherWorkstation()
    {
        var (projectId, task) = await SeedTaskAsync("Running sync on a different workstation");
        var selectedDevice = await SeedGitSyncRuntimeAsync(projectId);
        var otherUser = User.Create("running-other-git-sync", $"running-other-git-sync-{Guid.NewGuid():N}@beacon.local", "hash");
        var otherDevice = DaemonDevice.Create(
            "external running git-sync workstation",
            otherUser.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        otherDevice.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"agentRuntime":"external"}""");
        var success = WorkstationCommand.Create(
            selectedDevice.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var sha = new string('a', 40);
        success.Succeed(JsonSerializer.Serialize(
            new GitSyncResultDto(
                "main", "origin/main", 0, 0, true, false, true, false,
                sha, sha, GitSyncOutcome.UpToDate, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var running = WorkstationCommand.Create(
            otherDevice.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        running.Claim(TimeSpan.FromMinutes(15), "other-device-instance");

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(otherUser);
            _db.DaemonDevices.Add(otherDevice);
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(projectId, otherDevice.Id, "/workspace/other"));
            _db.WorkstationCommands.AddRange(success, running);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(selectedDevice.Id, result.Value!.Preflight!.PipelineDeviceId);
        Assert.False(result.Value.Preflight.RepositorySyncInProgress);
        Assert.False(result.Value.Preflight.RepositorySyncBlocked);
        Assert.NotEqual("PipelineRepositorySyncInProgress", result.Value.StartBlockedReason);
    }

    [Fact]
    public async Task GetPipeline_BlocksWhileGitSyncIsPendingEvenIfCapabilityIsNoLongerAdvertised()
    {
        var (projectId, task) = await SeedTaskAsync("Pending GitSync on selected workstation");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var pending = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        device.Heartbeat(probeJson: "{}");
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(pending);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.PipelineGitSyncSupported);
        Assert.True(result.Value!.Preflight!.RepositorySyncInProgress);
        Assert.True(result.Value.Preflight.RepositorySyncBlocked);
        Assert.Equal("PipelineRepositorySyncInProgress", result.Value.StartBlockedReason);
    }

    [Fact]
    public async Task GetPipeline_ReportsRuntimeRootChangesAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Runtime root changed after GitSync");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var sha = new string('a', 40);
        command.Succeed(JsonSerializer.Serialize(
            new GitSyncResultDto(
                "main", "origin/main", 0, 0, true, false, true, false,
                sha, sha, GitSyncOutcome.UpToDate, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(item => item.ProjectId == projectId && item.DeviceId == device.Id,
                    TestContext.Current.CancellationToken);
            runtime.SetLocalRoot("/workspace/new-checkout");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.RuntimeChanged, result.Value.Preflight.LastKnownRepositoryReasonCode);
    }

    [Fact]
    public async Task GetPipeline_ReportsInconsistentVerificationAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Inconsistent up-to-date verification");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var syncResult = new GitSyncResultDto(
            "main", "origin/main", 0, 0, true, false, true, false,
            new string('a', 40), new string('b', 40), GitSyncOutcome.UpToDate, null, null);
        command.Succeed(JsonSerializer.Serialize(
            syncResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.FetchFailed, result.Value.Preflight.LastKnownRepositoryReasonCode);
    }

    [Fact]
    public async Task GetPipeline_ReportsDirtyButUpToDateWorkingTreeAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Dirty fast-forward verification");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var syncResult = new GitSyncResultDto(
            "main", "origin/main", 0, 0, false, false, true, false,
            new string('a', 40), new string('a', 40), GitSyncOutcome.UpToDate, null, null);
        command.Succeed(JsonSerializer.Serialize(
            syncResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.DirtyWorkingTree, result.Value.Preflight.LastKnownRepositoryReasonCode);
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

    [Fact]
    public async Task GetPipeline_ReportsNoUpstreamAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("No upstream diagnosis");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var syncResult = new GitSyncResultDto(
            "main", null, 0, 0, true, false, false, false,
            new string('a', 40), new string('a', 40),
            GitSyncOutcome.Refused, GitSyncRefusalCode.NoUpstream, null);
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
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.NoUpstream, result.Value.Preflight.LastKnownRepositoryReasonCode);
    }

    [Fact]
    public async Task GetPipeline_ReportsUnknownRefusalCodeAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Unknown GitSync refusal code");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var malformedResult = new
        {
            branch = "main",
            upstream = "origin/main",
            ahead = 0,
            behind = 0,
            clean = true,
            detached = false,
            hasUpstream = true,
            diverged = false,
            headBefore = new string('a', 40),
            headAfter = new string('a', 40),
            outcome = GitSyncOutcome.UpToDate,
            reasonCode = 999,
            stderr = (string?)null
        };
        command.Succeed(JsonSerializer.Serialize(malformedResult));
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectId)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.FetchFailed, result.Value.Preflight.LastKnownRepositoryReasonCode);
    }

    [Fact]
    public async Task GetPipeline_ReportsDetachedHeadAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Detached HEAD diagnosis");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var syncResult = new GitSyncResultDto(
            null, null, 0, 0, true, true, false, false,
            new string('a', 40), new string('a', 40),
            GitSyncOutcome.Refused, GitSyncRefusalCode.Detached, null);
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
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.Detached, result.Value.Preflight.LastKnownRepositoryReasonCode);
    }

    [Fact]
    public async Task PipelinePreflightAndLaunch_UseTheSameNativeWorkstationWithoutGitSync()
    {
        var (projectId, task) = await SeedTaskAsync("Native runtime selection");
        var nativeDevice = await SeedGitSyncRuntimeAsync(projectId);

        // Make an external-agent workstation newer and give it a successful GitSync result.
        // It must not be selected for a pipeline that requires a native workstation.
        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
        var externalUser = User.Create("external", $"external-{Guid.NewGuid():N}@beacon.local", "hash");
        var externalDevice = DaemonDevice.Create(
            "external-agent workstation",
            externalUser.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        externalDevice.Heartbeat(
            """{"capabilities":["git-sync-v1"]}""",
            """{"AgentRuntime":"opencode"}""");
        var externalRuntime = ProjectRuntime.Create(projectId, externalDevice.Id, "/workspace/external");

        var externalSync = WorkstationCommand.Create(
            externalDevice.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var sha = new string('b', 40);
        var verifiedExternalResult = new GitSyncResultDto(
            "main", "origin/main", 0, 0, true, false, true, false,
            sha, sha, GitSyncOutcome.UpToDate, null, null);
        externalSync.Succeed(JsonSerializer.Serialize(
            verifiedExternalResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var backend = LocalModelBackend.Create(
            "native-selection-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            externalUser.Id);
        var session = PipelineSession.Create(
            task.Id, projectId, PipelineRole.Planner, "Planner prompt", modelBackendId: backend.Id);

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(externalUser);
            _db.DaemonDevices.Add(externalDevice);
            _db.ProjectRuntimes.Add(externalRuntime);
            _db.WorkstationCommands.Add(externalSync);
            _db.LocalModelBackends.Add(backend);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var preflight = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);

        Assert.True(preflight.Success, preflight.Error);
        Assert.Equal(nativeDevice.Id, preflight.Value!.Preflight!.PipelineDeviceId);
        Assert.False(preflight.Value.Preflight.RepositorySyncBlocked);

        var launch = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(session.Id)), TestContext.Current.CancellationToken);

        Assert.True(launch.Success, launch.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(item => item.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Active, persisted.Status);
            var pipelineCommand = await _db.WorkstationCommands.AsNoTracking()
                .SingleAsync(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession,
                    TestContext.Current.CancellationToken);
            Assert.Equal(nativeDevice.Id, pipelineCommand.DeviceId);
        }
    }

    [Fact]
    public async Task GetPipeline_ReportsTimeoutAsWarning()
    {
        var (projectId, task) = await SeedTaskAsync("Git status timeout");
        var device = await SeedGitSyncRuntimeAsync(projectId);
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId, mode = "PullFastForward" }),
            projectId);
        var syncResult = new GitSyncResultDto(
            null, null, 0, 0, false, false, false, false,
            null, null, GitSyncOutcome.Failed, GitSyncRefusalCode.Timeout, "Git status timed out.");
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
        Assert.False(result.Value!.Preflight!.RepositorySyncBlocked);
        Assert.Equal(GitSyncRefusalCode.Timeout, result.Value.Preflight.LastKnownRepositoryReasonCode);
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
