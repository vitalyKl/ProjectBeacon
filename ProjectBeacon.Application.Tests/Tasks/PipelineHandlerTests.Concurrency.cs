namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    /// <summary>Spawner that signals entry and then blocks until released.</summary>
    private sealed class GateSpawner : ISessionSpawner
    {
        private readonly TaskCompletionSource<bool> _entered = new();
        private readonly TaskCompletionSource<bool> _release = new();

        public Task Entered => _entered.Task;
        public void Release() => _release.SetResult(true);

        public Task<SpawnedSession> SpawnAsync(IBeaconDb db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
        {
            _entered.TrySetResult(true);
            return Await();

            async Task<SpawnedSession> Await()
            {
                await _release.Task;
                return new SpawnedSession(null, null, null);
            }
        }
    }

    private sealed class FailingSpawner : ISessionSpawner
    {
        public Task<SpawnedSession> SpawnAsync(IBeaconDb db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
            => Task.FromException<SpawnedSession>(new InvalidOperationException("spawn exploded"));
    }

    [Fact]
    public async Task StartPipeline_ConcurrentStart_LoserFails_SingleSpawnSingleSession()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new GateSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = new StartPipelineHandler(factory, spawner);

        // First start claims the Planning stage and then waits inside the spawner.
        var first = Task.Run(() => start.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id))));
        await spawner.Entered;

        var second = await start.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        Assert.False(second.Success);

        spawner.Release();
        var firstResult = await first;
        Assert.True(firstResult.Success, firstResult.Error);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.PipelineSessions.CountAsync(s => s.TaskId == task.Id, TestContext.Current.CancellationToken));
            var stage = await _db.Tasks.AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => t.PipelineStage)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Planning, stage);
        }
    }

    [Fact]
    public async Task StartActorSession_SpawnFailure_RollsBackStageAndSubtask()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        var next = Subtask.Create("next", task.Id, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.Subtasks.Add(next);
            await _db.Tasks
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PipelineStage, TaskPipelineStage.ReopenedForRevision), TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new StartActorSessionHandler(factory, new FailingSpawner())
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, next.Id)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("spawn exploded", result.Error);

        using (TenantScope.EnterUnscoped())
        {
            var stage = await _db.Tasks.AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => t.PipelineStage)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, stage);
            var status = await _db.Subtasks.AsNoTracking()
                .Where(s => s.Id == next.Id)
                .Select(s => s.Status)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.Pending, status);
            Assert.Equal(0, await _db.PipelineSessions.CountAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ReviewCheck_SecondCheck_LosesClaim_NoSecondCommand()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var (user, device) = await SeedOnlineRuntimeAsync(projectId);
        await SeedOpenReviewRunAsync(projectId, task.Id);

        var first = await new RecordReviewCheckHandler(factory, new EnqueueCommandHandler(factory))
            .HandleAsync(new RecordReviewCheckCommand(
                new RecordReviewCheckRequest(task.Id, device.Id, user.Id, "dotnet test"),
                HumanActor(user.Id)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        var second = await new RecordReviewCheckHandler(factory, new EnqueueCommandHandler(factory))
            .HandleAsync(new RecordReviewCheckCommand(
                new RecordReviewCheckRequest(task.Id, device.Id, user.Id, "dotnet test"),
                HumanActor(user.Id)), TestContext.Current.CancellationToken);
        Assert.False(second.Success);
        Assert.Contains("already in flight", second.Error);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.WorkstationCommands
                .CountAsync(c => c.DeviceId == device.Id && c.Kind == WorkstationCommandKind.RunReviewCheck, TestContext.Current.CancellationToken));
        }
    }
}
