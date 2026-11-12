namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Application.Authorization;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task ReportSubtaskResult_PersistsServerWorkspaceReport()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "implement the requested feature")), TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, subtask.Value!.Id)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var result = await new ReportSubtaskResultHandler(factory).HandleAsync(
            new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
                task.Id, subtask.Value.Id, "diff://verified", "Implemented and verified the feature.",
                "Implementation completed")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(SubtaskStatus.Done, result.Value!.Subtasks.Single().Status);
        using (TenantScope.EnterUnscoped())
        {
            var report = await _db.TaskWorkspaceSections.AsNoTracking()
                .SingleAsync(section => section.TaskId == task.Id && section.AuthorSubtaskId == subtask.Value.Id,
                    TestContext.Current.CancellationToken);
            Assert.Equal("report/" + subtask.Value.Id.ToString("N") + "/attempt-1", report.Key);
            Assert.Equal("Report", report.Kind);
            Assert.Equal("Server", report.AuthorType);
            Assert.Equal("Actor", report.AuthorRole);
            Assert.Equal("Implementation completed", report.Abstract);
            Assert.Equal("Implemented and verified the feature.", report.Content);
            Assert.Equal(actor.Value!.Id, report.AuthorSessionId);
        }
    }

    [Fact]
    public async Task Verdict_MissingNoteOrSubtask_Fails()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);
        await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value!.Id, "diff://a", "ok")), TestContext.Current.CancellationToken);
        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)), TestContext.Current.CancellationToken);
        Assert.True(review.Success, review.Error);

        var noNote = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "  ")), TestContext.Current.CancellationToken);
        Assert.False(noNote.Success);
        Assert.Equal("Verdict note is required.", noNote.Error);

        var reopenWithoutSub = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.ReopenSubtask, "fix it")), TestContext.Current.CancellationToken);
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
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);
            Assert.True(review.Success, review.Error);

            var reopened = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "Fix the null check", done.SubtaskId)), TestContext.Current.CancellationToken);
            Assert.True(reopened.Success, reopened.Error);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, reopened.Value!.Stage);
            Assert.Equal(TaskItemStatus.InProgress, reopened.Value.Status);
            var subtask = reopened.Value.Subtasks.Single(s => s.Id == done.SubtaskId);
            Assert.Equal(SubtaskStatus.Pending, subtask.Status);
            Assert.Equal(1, subtask.ReopenCount);
            Assert.Contains("Fix the null check", subtask.Instructions);
            Assert.Single(reopened.Value.Verdicts);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(done.Task.Id, done.SubtaskId)), TestContext.Current.CancellationToken);
            Assert.True(actor.Success, actor.Error);
            Assert.Equal(TaskPipelineStage.Executing,
                (await new GetPipelineHandler(factory).HandleAsync(new GetPipelineCommand(new GetPipelineRequest(done.Task.Id)), TestContext.Current.CancellationToken)).Value!.Stage);

            var redone = await new ReportSubtaskResultHandler(factory)
                .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(done.Task.Id, done.SubtaskId, "diff://a2", "fixed")), TestContext.Current.CancellationToken);
            Assert.True(redone.Success, redone.Error);
            Assert.Equal(SubtaskStatus.Done, redone.Value!.Subtasks.Single(s => s.Id == done.SubtaskId).Status);
            using (TenantScope.EnterUnscoped())
            {
                var reports = await _db.TaskWorkspaceSections.AsNoTracking()
                    .Where(section => section.TaskId == done.Task.Id
                        && section.AuthorSubtaskId == done.SubtaskId
                        && section.Kind == "Report")
                    .OrderBy(section => section.Key)
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, reports.Count);
                Assert.Contains(reports, report => report.Key.EndsWith("/attempt-1", StringComparison.Ordinal));
                Assert.Contains(reports, report => report.Key.EndsWith("/attempt-2", StringComparison.Ordinal));
            }

            var review2 = await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);
            Assert.True(review2.Success, review2.Error);
            var approved = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.Approve, "Good now")), TestContext.Current.CancellationToken);
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
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);
            var first = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "note 1", done.SubtaskId)), TestContext.Current.CancellationToken);
            Assert.True(first.Success, first.Error);
            Assert.Equal(1, first.Value!.Subtasks.Single(s => s.Id == done.SubtaskId).ReopenCount);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(done.Task.Id, done.SubtaskId)), TestContext.Current.CancellationToken);
            Assert.True(actor.Success, actor.Error);
            await new ReportSubtaskResultHandler(factory)
                .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(done.Task.Id, done.SubtaskId, "diff://b", "ok")), TestContext.Current.CancellationToken);
            await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);

            var second = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "note 2", done.SubtaskId)), TestContext.Current.CancellationToken);
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
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);
            await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "stuck", done.SubtaskId)), TestContext.Current.CancellationToken);

            var stranger = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, Guid.NewGuid().ToString(), "cleanup"),
                    new ActorContext(ActorType.Human, Guid.NewGuid(), false, null, null, done.ProjectId, null, ApiTokenCapability.None)), TestContext.Current.CancellationToken);
            Assert.False(stranger.Success);
            Assert.Equal("Forbidden.", stranger.Error);

            var nonAdmin = User.Create("member", "member@example.com", "hash");
            var admin = User.Create("root", "root@example.com", "hash", isAdmin: true);
            using (TenantScope.EnterUnscoped())
            {
                _db.Users.AddRange(nonAdmin, admin);
                _db.ProjectMembers.Add(ProjectMember.Create(done.ProjectId, nonAdmin.Id, MemberRole.Member));
                await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var member = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, nonAdmin.Id.ToString(), "cleanup"),
                    new ActorContext(ActorType.Human, nonAdmin.Id, false, null, null, done.ProjectId, null, ApiTokenCapability.None)), TestContext.Current.CancellationToken);
            Assert.False(member.Success);
            Assert.Equal("Forbidden.", member.Error);

            var asAdmin = await new ForceClosePipelineHandler(factory)
                .HandleAsync(new ForceClosePipelineCommand(
                    new ForceClosePipelineRequest(done.Task.Id, admin.Id.ToString(), "obsolete"),
                    new ActorContext(ActorType.Human, admin.Id, true, null, null, done.ProjectId, null, ApiTokenCapability.None)), TestContext.Current.CancellationToken);
            Assert.True(asAdmin.Success, asAdmin.Error);
            Assert.Equal(TaskPipelineStage.Closed, asAdmin.Value!.Stage);
            Assert.Equal(TaskItemStatus.Done, asAdmin.Value.Status);
            Assert.Equal("force-closed without review: obsolete", asAdmin.Value.ReviewNotes);
        }
    }

    [Fact]
    public async Task ForceClose_ScopedAdminToken_ClosesWithoutReasonMarker()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)), TestContext.Current.CancellationToken);
        await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(
                new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.ReopenSubtask, "stuck", done.SubtaskId)), TestContext.Current.CancellationToken);

        var tokenId = Guid.NewGuid();
        var result = await new ForceClosePipelineHandler(factory)
            .HandleAsync(new ForceClosePipelineCommand(
                new ForceClosePipelineRequest(done.Task.Id, tokenId.ToString()),
                new ActorContext(ActorType.ApiToken, null, false, tokenId, null,
                    done.ProjectId, null, ApiTokenCapability.Admin)), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        Assert.Equal(TaskPipelineStage.Closed, result.Value!.Stage);
        Assert.Equal("force-closed without review", result.Value.ReviewNotes);
    }
    [Fact]
    public async Task LaunchSession_DoubleLaunch_Fails()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var spawner = new FakeSpawner(backendId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        Assert.True(start.Success, start.Error);

        var first = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(start.Value!.Id)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        var second = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(start.Value.Id)), TestContext.Current.CancellationToken);
        Assert.False(second.Success);
        Assert.Contains("Cannot launch", second.Error);

        var missing = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(Guid.NewGuid())), TestContext.Current.CancellationToken);
        Assert.False(missing.Success);
        Assert.Equal("Session not found.", missing.Error);
    }
}
