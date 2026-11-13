namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
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
        var created = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-1", user.Id)), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        Assert.StartsWith("bcd_", created.Value!.Token);
        Assert.Equal(user.Id, created.Value.UserId);

        var listed = await new ListDevicesHandler(Factory()).HandleAsync(new ListDevicesCommand(new ListDevicesRequest(user.Id)), TestContext.Current.CancellationToken);
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
        var first = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("a", "fp", user.Id)), TestContext.Current.CancellationToken);
        var second = await create.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest("b", "fp", user.Id)), TestContext.Current.CancellationToken);
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
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(created.Value!.Id, user.Id, WorkstationCommandKind.Probe, null, null)), TestContext.Current.CancellationToken);
        Assert.False(queued.Success);
        Assert.Equal("Device is not connected.", queued.Error);
    }

    [Fact]
    public async Task Heartbeat_Claim_Complete_RoundTrip()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        var deviceId = created.Value!.Id;

        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{\"git\":\"git\"}", "{}")), TestContext.Current.CancellationToken);
        Assert.True(beat.Success, beat.Error);
        Assert.True(beat.Value!.Online);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(deviceId, user.Id, WorkstationCommandKind.ListDir, "{\"path\":\"\"}", null)), TestContext.Current.CancellationToken);
        Assert.True(queued.Success, queued.Error);

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.True(claimed.Success, claimed.Error);
        Assert.NotNull(claimed.Value);
        Assert.Equal(WorkstationCommandStatus.Running, claimed.Value!.Status);

        var empty = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.Null(empty.Value);

        var done = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(claimed.Value.Id, deviceId, true, "{\"entries\":[]}", null)), TestContext.Current.CancellationToken);
        Assert.True(done.Success, done.Error);
        Assert.Equal(WorkstationCommandStatus.Succeeded, done.Value!.Status);

        var got = await new GetCommandHandler(Factory()).HandleAsync(
            new GetCommandCommand(new GetCommandRequest(claimed.Value.Id, user.Id)), TestContext.Current.CancellationToken);
        Assert.Equal("{\"entries\":[]}", got.Value!.ResultJson);
    }

    [Fact]
    public async Task AttachRuntime_PerDeviceRoot()
    {
        var (user, project) = await SeedMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value!.Id, user.Id, @"A:\work\app")), TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);
        Assert.Equal(@"A:\work\app", attached.Value!.LocalRoot);

        var again = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, created.Value.Id, user.Id, @"A:\work\other")), TestContext.Current.CancellationToken);
        Assert.Equal(attached.Value.Id, again.Value!.Id);
        Assert.Equal(@"A:\work\other", again.Value.LocalRoot);

        var listed = await new ListRuntimesHandler(Factory()).HandleAsync(
            new ListRuntimesCommand(new ListRuntimesRequest(project.Id, user.Id)), TestContext.Current.CancellationToken);
        Assert.Single(listed.Value!);

        var detached = await new DetachRuntimeHandler(Factory()).HandleAsync(
            new DetachRuntimeCommand(new DetachRuntimeRequest(listed.Value![0].Id, user.Id)), TestContext.Current.CancellationToken);
        Assert.True(detached.Success, detached.Error);
    }

    [Fact]
    public async Task EnqueueProjectRuntimeCommand_CapturesRootAndBlocksConcurrentRuntimeMutation()
    {
        var (user, project) = await SeedMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("runtime-command-device", "fp-" + Guid.NewGuid().ToString("N"), user.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        var deviceId = created.Value!.Id;

        var heartbeat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", "{}")),
            TestContext.Current.CancellationToken);
        Assert.True(heartbeat.Success, heartbeat.Error);

        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, "/workspace/original")),
            TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);

        var queued = await new EnqueueCommandHandler(Factory()).HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId,
                user.Id,
                WorkstationCommandKind.ChatEnsureSession,
                JsonSerializer.Serialize(new { title = "Runtime-bound chat" }),
                project.Id)),
            TestContext.Current.CancellationToken);

        Assert.True(queued.Success, queued.Error);
        Assert.Equal("/workspace/original", queued.Value!.LocalRoot);

        var changed = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, deviceId, user.Id, "/workspace/replacement")),
            TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(item => item.ProjectId == project.Id && item.DeviceId == deviceId,
                    TestContext.Current.CancellationToken);
            Assert.Equal("/workspace/original", runtime.LocalRoot);
            var command = await _db.WorkstationCommands.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == queued.Value.Id, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
        }
    }

    [Fact]
    public async Task AttachRuntime_RejectsRootChangeWhileGitSyncIsPending()
    {
        var (user, project) = await SeedMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(device.Success, device.Error);
        var attach = new AttachRuntimeHandler(Factory());
        var initial = await attach.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Value!.Id, user.Id, "/workspace/original")),
            TestContext.Current.CancellationToken);
        Assert.True(initial.Success, initial.Error);

        var command = WorkstationCommand.Create(
            device.Value.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
            project.Id,
            user.Id);
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var changed = await attach.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Value.Id, user.Id, "/workspace/replacement")),
            TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(item => item.ProjectId == project.Id && item.DeviceId == device.Value.Id,
                    TestContext.Current.CancellationToken);
            Assert.Equal("/workspace/original", runtime.LocalRoot);
        }
    }

    [Fact]
    public async Task DetachRuntime_RejectsRemovalWhileGitSyncIsRunning()
    {
        var (user, project) = await SeedMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(device.Success, device.Error);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Value!.Id, user.Id, "/workspace/project")),
            TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);

        var command = WorkstationCommand.Create(
            device.Value.Id,
            WorkstationCommandKind.GitSync,
            JsonSerializer.Serialize(new { projectId = project.Id, mode = "PullFastForward" }),
            project.Id,
            user.Id);
        command.Claim(TimeSpan.FromMinutes(15), "git-sync-test-instance");
        using (TenantScope.EnterUnscoped())
        {
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var detached = await new DetachRuntimeHandler(Factory()).HandleAsync(
            new DetachRuntimeCommand(new DetachRuntimeRequest(attached.Value!.Id, user.Id)),
            TestContext.Current.CancellationToken);

        Assert.False(detached.Success);
        Assert.Equal(ErrorKind.Conflict, detached.Kind);
        using (TenantScope.EnterUnscoped())
        {
            Assert.True(await _db.ProjectRuntimes.IgnoreQueryFilters()
                .AnyAsync(runtime => runtime.Id == attached.Value.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AttachRuntime_RejectsRootChangeWhilePipelineSessionIsReady()
    {
        var (user, project) = await SeedMemberAsync();
        var device = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("ready-pipeline-device", "fp-" + Guid.NewGuid().ToString("N"), user.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(device.Success, device.Error);
        var attached = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Value!.Id, user.Id, "/workspace/original")),
            TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);

        var task = TaskItem.Create("Ready Planner task", project.Id);
        var session = PipelineSession.Create(task.Id, project.Id, PipelineRole.Planner, "planner prompt");
        using (TenantScope.EnterUnscoped())
        {
            _db.Tasks.Add(task);
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var changed = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Value.Id, user.Id, "/workspace/replacement")),
            TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(item => item.ProjectId == project.Id && item.DeviceId == device.Value.Id,
                    TestContext.Current.CancellationToken);
            Assert.Equal("/workspace/original", runtime.LocalRoot);
        }
    }

    [Fact]
    public async Task AttachRuntime_RejectsRootChangeWhileChatSessionIsStreaming()
    {
        var (user, project, deviceId) = await OnlineProjectDeviceAsync();
        var projectId = project.Id;
        var userId = user.Id;
        var runtime = await new AttachRuntimeHandler(Factory()).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, deviceId, userId, @"A:\work")),
            TestContext.Current.CancellationToken);
        Assert.True(runtime.Success, runtime.Error);

        var chatSession = Domain.Entities.Chat.ChatSession.Create(projectId, deviceId, "Chat", @"A:\work");
        chatSession.SetExternalSessionId("oc_test");
        using (TenantScope.EnterUnscoped())
        {
            _db.ChatSessions.Add(chatSession);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var sessionId = chatSession.Id;
        var factory = HandlerSqlite.Factory(_connection);
        using (TenantScope.EnterUnscoped())
        {
            await using var db = factory.CreateDbContext();
            var session = await db.ChatSessions.SingleAsync(item => item.Id == sessionId,
                TestContext.Current.CancellationToken);
            session.SetStreaming();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var changed = await new AttachRuntimeHandler(factory).HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, deviceId, userId, @"B:\new-checkout")),
            TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(item => item.ProjectId == projectId && item.DeviceId == deviceId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(@"A:\work", runtime.LocalRoot);
        }
    }

    [Fact]
    public async Task Heartbeat_RecordsHostSample()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value!.Id,
                """{"hostLoad":{"cpuPercent":40,"ramUsedBytes":1,"ramTotalBytes":2,"sampledAt":"2026-09-20T12:00:00Z"}}""", "{}")), TestContext.Current.CancellationToken);
        Assert.True(beat.Success, beat.Error);
        var samples = await new ListHostSamplesHandler(Factory()).HandleAsync(
            new ListHostSamplesCommand(new ListHostSamplesRequest(user.Id, created.Value.Id)), TestContext.Current.CancellationToken);
        Assert.True(samples.Success, samples.Error);
        Assert.Single(samples.Value!);
        Assert.Equal(40, samples.Value![0].CpuPercent);
    }

    [Fact]
    public async Task Revoke_HidesFromHeartbeat()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        await new RevokeDeviceHandler(Factory()).HandleAsync(
            new RevokeDeviceCommand(new RevokeDeviceRequest(created.Value!.Id, user.Id)), TestContext.Current.CancellationToken);
        var beat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(created.Value.Id, null, null)), TestContext.Current.CancellationToken);
        Assert.False(beat.Success);
    }

    [Fact]
    public async Task Revoke_HidesFromList()
    {
        var user = await SeedUserAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp", user.Id)), TestContext.Current.CancellationToken);
        await new RevokeDeviceHandler(Factory()).HandleAsync(
            new RevokeDeviceCommand(new RevokeDeviceRequest(created.Value!.Id, user.Id)), TestContext.Current.CancellationToken);
        var listed = await new ListDevicesHandler(Factory()).HandleAsync(
            new ListDevicesCommand(new ListDevicesRequest(user.Id)), TestContext.Current.CancellationToken);
        Assert.True(listed.Success);
        Assert.Empty(listed.Value!);
    }
}
