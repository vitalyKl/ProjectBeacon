namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Devices;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task LaunchSession_NativeRuntime_QueuesScopedPipelineCommand_AndRejectsDuplicateLaunch()
    {
        var (projectId, task) = await SeedTaskAsync("Native pipeline task");
        var user = User.Create("native", $"native-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "native workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash",
            "bcd_" + Guid.NewGuid().ToString("N"));
        device.Heartbeat("{}", """{"agentRuntime":"native"}""");
        var backend = LocalModelBackend.Create(
            "native-test-model",
            ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf",
            8192,
            60,
            user.Id);
        var runtime = ProjectRuntime.Create(projectId, device.Id, "/workspace/native");
        var session = PipelineSession.Create(
            task.Id,
            projectId,
            PipelineRole.Planner,
            "Use only the task and project context in this prompt.",
            modelBackendId: backend.Id);

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
        var handler = new LaunchSessionHandler(factory);
        var launched = await handler.HandleAsync(
            new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
            TestContext.Current.CancellationToken);

        Assert.True(launched.Success, launched.Error);
        Assert.Equal(SessionStatus.Active, launched.Value!.Status);
        Assert.NotNull(launched.Value.LaunchedAt);

        using (TenantScope.EnterUnscoped())
        {
            var command = await _db.WorkstationCommands.AsNoTracking()
                .SingleAsync(c => c.Kind == WorkstationCommandKind.RunPipelineSession
                    && c.ProjectId == projectId
                    && c.DeviceId == device.Id,
                    TestContext.Current.CancellationToken);

            Assert.Equal(WorkstationCommandStatus.Pending, command.Status);
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(projectId, root.GetProperty("projectId").GetGuid());
            Assert.Equal(task.Id, root.GetProperty("taskId").GetGuid());
            Assert.Equal(session.Id, root.GetProperty("sessionId").GetGuid());
            Assert.Equal("Planner", root.GetProperty("role").GetString());
            Assert.Equal("Use only the task and project context in this prompt.", root.GetProperty("prompt").GetString());
            Assert.Equal("beacon-local/native-test-model", root.GetProperty("model").GetString());
            Assert.Equal(8192, root.GetProperty("contextLimit").GetInt32());
        }

        var duplicate = await handler.HandleAsync(
            new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
            TestContext.Current.CancellationToken);
        Assert.False(duplicate.Success);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.WorkstationCommands.AsNoTracking()
                .CountAsync(c => c.Kind == WorkstationCommandKind.RunPipelineSession
                    && c.ProjectId == projectId
                    && c.DeviceId == device.Id,
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LaunchSession_WithoutNativeRuntime_FailsAndLeavesSessionReady()
    {
        var (projectId, task) = await SeedTaskAsync("Pipeline without native runtime");
        var session = PipelineSession.Create(
            task.Id,
            projectId,
            PipelineRole.Planner,
            "Planner prompt",
            modelBackendId: Guid.NewGuid());

        using (TenantScope.EnterUnscoped())
        {
            _db.PipelineSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var launched = await new LaunchSessionHandler(factory).HandleAsync(
            new LaunchSessionCommand(new LaunchSessionRequest(session.Id)),
            TestContext.Current.CancellationToken);

        Assert.False(launched.Success);
        Assert.Contains("No native pipeline workstation", launched.Error);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Ready, persisted.Status);
            Assert.Null(persisted.LaunchedAt);
            Assert.Empty(await _db.WorkstationCommands.AsNoTracking()
                .Where(c => c.Kind == WorkstationCommandKind.RunPipelineSession)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }
}
