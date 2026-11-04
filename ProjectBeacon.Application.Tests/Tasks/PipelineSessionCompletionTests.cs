namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Devices;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
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
            Assert.Equal(SessionStatus.Closed, stored.Status);
            Assert.NotNull(stored.ClosedAt);
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
            Assert.Equal("native agent turn failed", stored.FailureReason);
            Assert.NotNull(stored.LastActivityAt);
        }
    }

    [Fact]
    public async Task FailedPlannerAndActorCommands_AdvancePendingSubtasksOneAtATime()
    {
        var (projectId, task) = await SeedTaskAsync("Recover a partially planned pipeline");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner(backendId);

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);
        var first = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "First actor work")), TestContext.Current.CancellationToken);
        var second = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Second actor work")), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);

        Guid plannerCommandId;
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(r => r.ProjectId == projectId, TestContext.Current.CancellationToken);
            var device = await _db.DaemonDevices.IgnoreQueryFilters()
                .SingleAsync(d => d.Id == runtime.DeviceId, TestContext.Current.CancellationToken);
            var plannerSession = await _db.PipelineSessions
                .SingleAsync(s => s.Id == planner.Value!.Id, TestContext.Current.CancellationToken);
            plannerSession.Launch();
            var command = WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.RunPipelineSession,
                JsonSerializer.Serialize(new { sessionId = plannerSession.Id }),
                projectId,
                device.UserId);
            command.Claim(TimeSpan.FromMinutes(CommandLease.PipelineMinutes), "test-instance");
            plannerCommandId = command.Id;
            _db.WorkstationCommands.Add(command);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var dispatcher = new PipelineActorQueueDispatcher(factory, spawner);
        var completion = new CompleteCommandHandler(factory, dispatcher);
        var deviceId = await DeviceForProjectAsync(projectId);
        var plannerFailed = await completion.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                plannerCommandId, deviceId, false, null,
                "planner returned HTTP 500", Generation: 1)),
            TestContext.Current.CancellationToken);
        Assert.True(plannerFailed.Success, plannerFailed.Error);

        var manualSecondStart = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, second.Value!.Id)),
                TestContext.Current.CancellationToken);
        Assert.False(manualSecondStart.Success);
        Assert.Contains("Another Actor session", manualSecondStart.Error);

        Guid firstActorCommandId;
        Guid firstActorSessionId;
        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var actorSession = await _db.PipelineSessions.IgnoreQueryFilters()
                .SingleAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor
                    && s.Status == SessionStatus.Active, TestContext.Current.CancellationToken);
            firstActorSessionId = actorSession.Id;
            var actorCommand = await _db.WorkstationCommands.IgnoreQueryFilters()
                .SingleAsync(c => c.Kind == WorkstationCommandKind.RunPipelineSession
                    && c.Status == WorkstationCommandStatus.Pending
                    && c.PayloadJson.Contains(firstActorSessionId.ToString()), TestContext.Current.CancellationToken);
            actorCommand.Claim(TimeSpan.FromMinutes(CommandLease.PipelineMinutes), "test-instance");
            firstActorCommandId = actorCommand.Id;
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var statuses = await _db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TaskId == task.Id).OrderBy(s => s.CreatedAt)
                .Select(s => s.Status).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([SubtaskStatus.InProgress, SubtaskStatus.Pending], statuses);
            Assert.Equal(1, await _db.PipelineSessions.IgnoreQueryFilters().CountAsync(
                s => s.TaskId == task.Id && s.Role == PipelineRole.Actor && s.Status == SessionStatus.Active,
                TestContext.Current.CancellationToken));
        }

        var actorFailed = await completion.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(
                firstActorCommandId, deviceId, false, null,
                "The pipeline agent turn exceeded the workstation chat timeout.", Generation: 1)),
            TestContext.Current.CancellationToken);
        Assert.True(actorFailed.Success, actorFailed.Error);

        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var subtasks = await _db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TaskId == task.Id).OrderBy(s => s.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.Failed, subtasks[0].Status);
            Assert.Equal("The pipeline agent turn exceeded the workstation chat timeout.", subtasks[0].Summary);
            Assert.Equal(SubtaskStatus.InProgress, subtasks[1].Status);

            var actors = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, actors.Count);
            Assert.Single(actors.Where(s => s.Status == SessionStatus.Failed));
            Assert.Single(actors.Where(s => s.Status == SessionStatus.Active));
        }
    }

    private async Task<Guid> DeviceForProjectAsync(Guid projectId)
    {
        using (TenantScope.EnterUnscoped())
        {
            var runtime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .SingleAsync(r => r.ProjectId == projectId, TestContext.Current.CancellationToken);
            return runtime.DeviceId;
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
            Assert.Equal("Pipeline command completed without a usable external session id.", stored.FailureReason);
            Assert.NotNull(stored.LastActivityAt);
            Assert.Null(stored.ExternalSessionId);
        }
    }
}
