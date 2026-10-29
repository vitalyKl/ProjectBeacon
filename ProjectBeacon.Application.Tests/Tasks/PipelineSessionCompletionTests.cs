namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    private async Task<(Guid ProjectId, PipelineSession Session, WorkstationCommand Command, Guid DeviceId)>
        SeedActivePipelineCommandAsync(string title)
    {
        var (projectId, task) = await SeedTaskAsync(title);
        var user = User.Create("native", $"native-{Guid.NewGuid():N}@beacon.local", "hash");
        var device = DaemonDevice.Create(
            "native workstation",
            user.Id,
            "fp-" + Guid.NewGuid().ToString("N"),
            "hash",
            "bcd_" + Guid.NewGuid().ToString("N"));
        var session = PipelineSession.Create(task.Id, projectId, PipelineRole.Planner, "Execute the pipeline prompt.");
        session.Launch();
        var command = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.RunPipelineSession,
            JsonSerializer.Serialize(new { sessionId = session.Id }),
            projectId,
            user.Id);
        command.Claim(TimeSpan.FromMinutes(15), "test-instance");

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.Add(user);
            _db.DaemonDevices.Add(device);
            _db.PipelineSessions.Add(session);
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return (projectId, session, command, device.Id);
    }

    [Fact]
    public async Task CompletePipelineSession_Success_PersistsExternalSessionId()
    {
        var (projectId, session, command, deviceId) = await SeedActivePipelineCommandAsync("Native completion success");
        var externalSessionId = "native-session-" + Guid.NewGuid().ToString("N");
        var resultJson = JsonSerializer.Serialize(new
        {
            pipelineSessionId = session.Id,
            externalSessionId
        });
        var handler = new CompleteCommandHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                command.Id, deviceId, true, resultJson, null, Generation: 1)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var stored = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(externalSessionId, stored.ExternalSessionId);
            Assert.Equal(SessionStatus.Active, stored.Status);
        }

        var duplicate = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                command.Id, deviceId, true, resultJson, null, Generation: 1)),
            TestContext.Current.CancellationToken);
        Assert.False(duplicate.Success);
    }

    [Fact]
    public async Task CompletePipelineSession_Failure_MarksActiveSessionFailed()
    {
        var (projectId, session, command, deviceId) = await SeedActivePipelineCommandAsync("Native completion failure");
        var handler = new CompleteCommandHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                command.Id, deviceId, false, null, "native agent turn failed", Generation: 1)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var stored = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Failed, stored.Status);
            Assert.NotNull(stored.ClosedAt);
        }
    }

    [Fact]
    public async Task CompletePipelineSession_SuccessWithoutExternalId_FailsSession()
    {
        var (projectId, session, command, deviceId) = await SeedActivePipelineCommandAsync("Native completion malformed");
        var invalidResult = JsonSerializer.Serialize(new { pipelineSessionId = session.Id });
        var handler = new CompleteCommandHandler(HandlerSqlite.Factory(_connection, Scope(projectId)));

        var result = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                command.Id, deviceId, true, invalidResult, null, Generation: 1)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var stored = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Failed, stored.Status);
            Assert.NotNull(stored.ClosedAt);
            Assert.Null(stored.ExternalSessionId);
        }
    }
}
