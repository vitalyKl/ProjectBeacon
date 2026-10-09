namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.Data.Sqlite;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task Verdict_MissingNoteOrSubtask_Fails()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value!.Id, "diff://a", "ok")));
        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);

        var noNote = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "  ")));
        Assert.False(noNote.Success);
        Assert.Equal("Verdict note is required.", noNote.Error);

        var reopenWithoutSub = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.ReopenSubtask, "fix it")));
        Assert.False(reopenWithoutSub.Success);
        Assert.Contains("SubtaskId is required", reopenWithoutSub.Error);
    }
    private const string ReopenCyclesEnv = "BEACON_MAX_REOPEN_CYCLES";
    private const string WorkerTokenEnv = "BEACON_WORKER_TOKEN";

    private sealed class EnvGuard : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;
        private readonly string? _value;

        public EnvGuard(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            _value = value;
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class DoneSubtask
    {
        public required Guid ProjectId { get; init; }
        public required TaskItem Task { get; init; }
        public required Guid SubtaskId { get; init; }
    }

    private async Task<DoneSubtask> SeedDoneSubtaskAsync(string title = "T")
    {
        var (projectId, task) = await SeedTaskAsync(title);
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value!.Id, "diff://a", "ok")));
        Assert.True(reported.Success, reported.Error);
        return new DoneSubtask { ProjectId = projectId, Task = task, SubtaskId = sub.Value.Id };
    }

    [Fact]
    public async Task Verdict_ReopenSubtask_ReopensForRevisionThenReworks()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        using (new EnvGuard(ReopenCyclesEnv, null))
        {
            var review = await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
            Assert.True(review.Success, review.Error);

            var reopened = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "Fix the null check", done.SubtaskId)));
            Assert.True(reopened.Success, reopened.Error);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, reopened.Value!.Stage);
            Assert.Equal(TaskItemStatus.InProgress, reopened.Value.Status);
            var subtask = reopened.Value.Subtasks.Single(s => s.Id == done.SubtaskId);
            Assert.Equal(SubtaskStatus.Pending, subtask.Status);
            Assert.Equal(1, subtask.ReopenCount);
            Assert.Contains("Fix the null check", subtask.Instructions);
            Assert.Single(reopened.Value.Verdicts);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(done.Task.Id, done.SubtaskId)));
            Assert.True(actor.Success, actor.Error);
            Assert.Equal(TaskPipelineStage.Executing,
                (await new GetPipelineHandler(factory).HandleAsync(new GetPipelineCommand(new GetPipelineRequest(done.Task.Id)))).Value!.Stage);

            var redone = await new ReportSubtaskResultHandler(factory)
                .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(done.Task.Id, done.SubtaskId, "diff://a2", "fixed")));
            Assert.True(redone.Success, redone.Error);
            Assert.Equal(SubtaskStatus.Done, redone.Value!.Subtasks.Single(s => s.Id == done.SubtaskId).Status);

            var review2 = await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
            Assert.True(review2.Success, review2.Error);
            var approved = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.Approve, "Good now")));
            Assert.True(approved.Success, approved.Error);
            Assert.Equal(TaskPipelineStage.Approved, approved.Value!.Stage);
        }
    }

    [Fact]
    public async Task Verdict_ReopenSubtask_LimitExceeded_ForceFailsSubtask()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        using (new EnvGuard(ReopenCyclesEnv, "1"))
        {
            await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
            var first = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "note 1", done.SubtaskId)));
            Assert.True(first.Success, first.Error);
            Assert.Equal(1, first.Value!.Subtasks.Single(s => s.Id == done.SubtaskId).ReopenCount);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(done.Task.Id, done.SubtaskId)));
            Assert.True(actor.Success, actor.Error);
            await new ReportSubtaskResultHandler(factory)
                .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(done.Task.Id, done.SubtaskId, "diff://b", "ok")));
            await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));

            var second = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "note 2", done.SubtaskId)));
            Assert.True(second.Success, second.Error);
            var subtask = second.Value!.Subtasks.Single(s => s.Id == done.SubtaskId);
            Assert.Equal(SubtaskStatus.Failed, subtask.Status);
            Assert.Contains("Reopen limit", subtask.Summary!);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, second.Value.Stage);
        }
    }

    [Fact]
    public async Task ForceClose_RequiresPrivilegeAndClosesStuckPipeline()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        using (new EnvGuard(WorkerTokenEnv, null))
        {
            await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
            await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "stuck", done.SubtaskId)));

            var stranger = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, Guid.NewGuid().ToString(), "cleanup")));
            Assert.False(stranger.Success);
            Assert.Equal("Force close requires admin privileges.", stranger.Error);

            var nonAdmin = User.Create("member", "member@example.com", "hash");
            var admin = User.Create("root", "root@example.com", "hash", isAdmin: true);
            using (TenantScope.EnterUnscoped())
            {
                _db.Users.AddRange(nonAdmin, admin);
                await _db.SaveChangesAsync();
            }

            var member = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, nonAdmin.Id.ToString(), "cleanup")));
            Assert.False(member.Success);
            Assert.Equal("Force close requires admin privileges.", member.Error);

            var asAdmin = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, admin.Id.ToString(), "obsolete")));
            Assert.True(asAdmin.Success, asAdmin.Error);
            Assert.Equal(TaskPipelineStage.Closed, asAdmin.Value!.Stage);
            Assert.Equal(TaskItemStatus.Done, asAdmin.Value.Status);
            Assert.Equal("force-closed without review: obsolete", asAdmin.Value.ReviewNotes);
        }
    }

    [Fact]
    public async Task ForceClose_WorkerToken_ClosesWithoutReasonMarker()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
        await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(
                new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "stuck", done.SubtaskId)));

        using (new EnvGuard(WorkerTokenEnv, "bcn_test_worker_token"))
        {
            var result = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, "bcn_test_worker_token")));
            Assert.True(result.Success, result.Error);
            Assert.Equal(TaskPipelineStage.Closed, result.Value!.Stage);
            Assert.Equal("force-closed without review", result.Value.ReviewNotes);
        }
    }
    [Fact]
    public async Task LaunchSession_DoubleLaunch_Fails()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);

        var first = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(start.Value!.Id)));
        Assert.True(first.Success, first.Error);
        var second = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(start.Value.Id)));
        Assert.False(second.Success);
        Assert.Contains("Cannot launch", second.Error);

        var missing = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(Guid.NewGuid())));
        Assert.False(missing.Success);
        Assert.Equal("Session not found.", missing.Error);
    }
}
