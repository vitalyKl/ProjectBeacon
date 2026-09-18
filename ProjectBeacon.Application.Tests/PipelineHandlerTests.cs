namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.Data.Sqlite;

public sealed class PipelineHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public PipelineHandlerTests()
    {
        // Handlers' contexts must see the fail-closed tenant filter,
        // so dispose the unscoped token right away.
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(Guid ProjectId, TaskItem Task)> SeedTaskAsync(string title, string? description = null)
    {
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("PipelineOrg", null);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("PipelineProject", null, org.Id);
            _db.Projects.Add(project);
            await _db.SaveChangesAsync();
            var task = TaskItem.Create(title, project.Id);
            task.Update(description: description);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync();
            return (project.Id, task);
        }
    }

    private static TenantContext Scope(Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, unscoped: false);
        return tenant;
    }

    private sealed class FakeSpawner : ISessionSpawner
    {
        public List<(Guid ProjectId, PipelineRole Role)> Calls { get; } = [];

        public Task<SpawnedSession> SpawnAsync(BeaconDbContext db, Guid projectId, PipelineRole role, CancellationToken ct)
        {
            Calls.Add((projectId, role));
            return Task.FromResult(new SpawnedSession(null, null));
        }
    }
    [Fact]
    public async Task HappyPath_PlannerActorReviewApprove_ClosesTaskDone()
    {
        var (projectId, task) = await SeedTaskAsync("Ship the report", "Generate the monthly report");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Equal(PipelineRole.Planner, start.Value!.Role);
        Assert.Equal(SessionStatus.Ready, start.Value.Status);
        Assert.Contains(PipelineRole.Planner, spawner.Calls.Select(c => c.Role));

        var sub1 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Collect data", new[] { "beacon_search_code" }, new[] { "data/" })));
        Assert.True(sub1.Success, sub1.Error);
        var sub2 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Render charts")));
        Assert.True(sub2.Success, sub2.Error);

        var actor1 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub1.Value!.Id)));
        Assert.True(actor1.Success, actor1.Error);
        Assert.Equal(PipelineRole.Actor, actor1.Value!.Role);
        Assert.Equal(sub1.Value.Id, actor1.Value.SubtaskId);

        var launched1 = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(actor1.Value.Id)));
        Assert.True(launched1.Success, launched1.Error);
        Assert.Equal(SessionStatus.Active, launched1.Value!.Status);
        Assert.NotNull(launched1.Value.LaunchedAt);

        var res1 = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub1.Value.Id, "diff://sub1", "Data collected")));
        Assert.True(res1.Success, res1.Error);
        Assert.Contains(res1.Value!.Subtasks, s => s.Id == sub1.Value!.Id && s.Status == SubtaskStatus.Done);
        Assert.Contains(res1.Value.Sessions, s => s.Id == actor1.Value!.Id && s.Status == SessionStatus.Closed);

        var actor2 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub2.Value!.Id)));
        Assert.True(actor2.Success, actor2.Error);
        var res2 = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub2.Value!.Id, "diff://sub2", "Charts rendered")));
        Assert.True(res2.Success, res2.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);
        Assert.Equal(PipelineRole.Review, review.Value!.Role);

        var verdict = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "Looks solid")));
        Assert.True(verdict.Success, verdict.Error);
        Assert.Equal(TaskPipelineStage.Approved, verdict.Value!.Stage);
        Assert.Equal(TaskItemStatus.Done, verdict.Value.Status);

        var closed = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.True(closed.Success, closed.Error);
        Assert.Equal(TaskPipelineStage.Closed, closed.Value!.Stage);
        Assert.Equal(TaskItemStatus.Done, closed.Value.Status);
        Assert.Equal("Looks solid", closed.Value.ReviewNotes);

        var state = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.True(state.Success, state.Error);
        Assert.Equal(TaskPipelineStage.Closed, state.Value!.Stage);
    }
    [Fact]
    public async Task Prompts_AreIsolated_PerRole()
    {
        var (projectId, task) = await SeedTaskAsync("Secret task title", "Secret task description");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Contains("PipelineProject", start.Value!.PromptContext);
        Assert.Contains("Secret task title", start.Value.PromptContext);
        Assert.Contains("Secret task description", start.Value.PromptContext);

        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Do the thing", new[] { "beacon_search_code" }, new[] { "src/" })));
        Assert.True(sub.Success, sub.Error);

        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        var actorPrompt = actor.Value!.PromptContext;
        Assert.Contains("Do the thing", actorPrompt);
        Assert.Contains("beacon_search_code", actorPrompt);
        Assert.Contains("src/", actorPrompt);
        Assert.DoesNotContain("Secret task title", actorPrompt);
        Assert.DoesNotContain("PipelineProject", actorPrompt);

        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value.Id, "diff://a", "Done it")));
        Assert.True(reported.Success, reported.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);
        var reviewPrompt = review.Value!.PromptContext;
        Assert.Contains("Secret task title", reviewPrompt);
        Assert.Contains("[DONE]", reviewPrompt);
        Assert.Contains("diff://a", reviewPrompt);
        Assert.DoesNotContain("Role: actor", reviewPrompt);
    }

    [Fact]
    public async Task ActorSession_BindsBackendAndBuildsLlamaCppLaunchSpec()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var backend = LocalModelBackend.Create("local-model", ModelBackendType.LlamaCpp, "llama-server -m m.gguf --port ${PORT}", 4096, 60, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }
        var spawner = new ManualSessionSpawner(new LlamaSwapOptions { Port = 8123, ProjectId = projectId });
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);
        Assert.Null(start.Value!.ModelBackendId);
        Assert.Null(start.Value.LaunchSpec);

        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        Assert.Equal(backend.Id, actor.Value!.ModelBackendId);
        Assert.Contains("--port 8123", actor.Value.LaunchSpec!);
        Assert.Contains("--ctx-size 4096", actor.Value.LaunchSpec);
        Assert.Contains("--no-reasoning-preserve", actor.Value.LaunchSpec);
    }

    [Fact]
    public async Task LaunchSpec_OpenAiCompatible_OmitsReasoningFlag()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var backend = LocalModelBackend.Create("openai-compat", ModelBackendType.OpenAiCompatible, "server --port ${PORT}", 0, 0, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.LocalModelBackends.Add(backend);
            _db.RoleBindings.Add(RoleBinding.Create(PipelineRole.Actor, backend.Id, projectId));
            await _db.SaveChangesAsync();
        }
        var spawner = new ManualSessionSpawner(new LlamaSwapOptions { Port = 8123, ProjectId = projectId });
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        Assert.Equal(backend.Id, actor.Value!.ModelBackendId);
        Assert.Contains("--port 8123", actor.Value.LaunchSpec!);
        Assert.DoesNotContain("--no-reasoning-preserve", actor.Value.LaunchSpec);
        Assert.DoesNotContain("--ctx-size", actor.Value.LaunchSpec);
    }
    [Fact]
    public async Task StageGuards_EnforcedAcrossHandlers()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        var subBeforeStart = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "early")));
        Assert.False(subBeforeStart.Success);
        Assert.Equal("Pipeline is not started.", subBeforeStart.Error);

        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);

        var restart = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.False(restart.Success);
        Assert.Contains("already started", restart.Error);

        var reviewTooEarly = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.False(reviewTooEarly.Success);

        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")));
        Assert.True(sub.Success, sub.Error);

        var reviewWithPending = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.False(reviewWithPending.Success);
        Assert.Contains("done or failed", reviewWithPending.Error);

        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.True(actor.Success, actor.Error);
        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value.Id, "diff://a", "ok")));
        Assert.True(reported.Success, reported.Error);
        Assert.Contains(reported.Value!.Subtasks, s => s.Id == sub.Value!.Id && s.Status == SubtaskStatus.Done);

        var verdictOutsideReview = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "late")));
        Assert.False(verdictOutsideReview.Success);
        Assert.Contains("Cannot record verdict", verdictOutsideReview.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);

        var approved = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "ok")));
        Assert.True(approved.Success, approved.Error);

        var secondVerdict = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.Approve, "again")));
        Assert.False(secondVerdict.Success);

        var doubleApprove = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.True(doubleApprove.Success, doubleApprove.Error);
        var approveAgain = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.False(approveAgain.Success);
    }

    [Fact]
    public async Task SubtaskGuards_AlreadyFinishedAndCrossTask()
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

        var actorAgain = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)));
        Assert.False(actorAgain.Success);
        Assert.Equal("Subtask is already finished.", actorAgain.Error);

        var reportAgain = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(task.Id, sub.Value!.Id, "diff://b", "again")));
        Assert.False(reportAgain.Success);
        Assert.Equal("Subtask is already finished.", reportAgain.Error);

        var otherTask = TaskItem.Create("Other", projectId);
        var foreignSubtask = Subtask.Create("foreign", otherTask.Id, projectId);
        using (TenantScope.EnterUnscoped())
        {
            _db.Tasks.Add(otherTask);
            _db.Subtasks.Add(foreignSubtask);
            await _db.SaveChangesAsync();
        }

        var foreignSub = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, foreignSubtask.Id)));
        Assert.False(foreignSub.Success);
        Assert.Equal("Subtask not found.", foreignSub.Error);
    }

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

    [Fact]
    public async Task PipelineHandlers_NoScope_FailClosed()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();

        var noTenant = new StartPipelineHandler(HandlerSqlite.Factory(_connection), spawner);
        var nullScope = await noTenant.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.False(nullScope.Success);
        Assert.Equal("Task not found.", nullScope.Error);

        var emptyGuidTenant = new TenantContext();
        emptyGuidTenant.Assign(Guid.Empty, null, unscoped: false);
        var emptyScope = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, emptyGuidTenant))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.False(emptyScope.Success);
        Assert.Equal("Task not found.", emptyScope.Error);
    }

    [Fact]
    public async Task PipelineHandlers_ForeignProject_Invisible()
    {
        var (projectIdA, _) = await SeedTaskAsync("A");
        var (projectIdB, taskB) = await SeedTaskAsync("B");
        var spawner = new FakeSpawner();
        var factoryA = HandlerSqlite.Factory(_connection, Scope(projectIdA));

        var getForeign = await new GetPipelineHandler(factoryA)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(taskB.Id)));
        Assert.False(getForeign.Success);
        Assert.Equal("Task not found.", getForeign.Error);

        var startForeign = await new StartPipelineHandler(factoryA, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(taskB.Id)));
        Assert.False(startForeign.Success);
        Assert.Equal("Task not found.", startForeign.Error);

        var getOwn = await new GetPipelineHandler(HandlerSqlite.Factory(_connection, Scope(projectIdB)))
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(taskB.Id)));
        Assert.True(getOwn.Success, getOwn.Error);
    }

    [Fact]
    public async Task EnterExecuting_Twice_IsIdempotent()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);

        var sub1 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "one")));
        Assert.True(sub1.Success, sub1.Error);
        var afterFirst = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.True(afterFirst.Success, afterFirst.Error);
        Assert.Equal(TaskPipelineStage.Executing, afterFirst.Value!.Stage);

        var sub2 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "two")));
        Assert.True(sub2.Success, sub2.Error);
        var afterSecond = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.True(afterSecond.Success, afterSecond.Error);
        Assert.Equal(TaskPipelineStage.Executing, afterSecond.Value!.Stage);

        var actor1 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub1.Value!.Id)));
        Assert.True(actor1.Success, actor1.Error);
        var actor2 = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub2.Value!.Id)));
        Assert.True(actor2.Success, actor2.Error);

        var final = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)));
        Assert.True(final.Success, final.Error);
        Assert.Equal(TaskPipelineStage.Executing, final.Value!.Stage);
        Assert.Equal(TaskItemStatus.InProgress, final.Value.Status);
    }

    [Fact]
    public async Task StartReview_Twice_SecondFails()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));

        var first = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
        Assert.True(first.Success, first.Error);
        Assert.Equal(PipelineRole.Review, first.Value!.Role);

        var second = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
        Assert.False(second.Success);
        Assert.Equal("Cannot start review in stage Reviewing.", second.Error);
    }

    [Fact]
    public async Task StartReview_AfterApproval_Fails()
    {
        var done = await SeedDoneSubtaskAsync();
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
        Assert.True(review.Success, review.Error);

        var approved = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(
                new RecordReviewVerdictRequest(done.Task.Id, ReviewVerdictKind.Approve, "ok")));
        Assert.True(approved.Success, approved.Error);
        Assert.Equal(TaskPipelineStage.Approved, approved.Value!.Stage);

        var late = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)));
        Assert.False(late.Success);
        Assert.Equal("Cannot start review in stage Approved.", late.Error);
    }

    [Fact]
    public async Task ReviewPrompt_MarksFailedSubtasks()
    {
        var (projectId, task) = await SeedTaskAsync("T", "Desc");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var start = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)));
        Assert.True(start.Success, start.Error);

        var sub1 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "done work")));
        var sub2 = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "failed work")));
        Assert.True(sub1.Success, sub1.Error);
        Assert.True(sub2.Success, sub2.Error);

        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(
                new ReportSubtaskResultRequest(task.Id, sub1.Value!.Id, "diff://ok", "all good")));
        Assert.True(reported.Success, reported.Error);

        var failed = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(
                new FailSubtaskRequest(task.Id, sub2.Value!.Id, "timed out")));
        Assert.True(failed.Success, failed.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)));
        Assert.True(review.Success, review.Error);
        var prompt = review.Value!.PromptContext;
        Assert.Contains($"[DONE] subtask {sub1.Value.Id}", prompt);
        Assert.Contains($"[FAILED - work not completed] subtask {sub2.Value.Id}", prompt);
        Assert.Contains("Summary: all good", prompt);
        Assert.Contains("Summary: timed out", prompt);
        Assert.Contains("DiffRef: diff://ok", prompt);
        Assert.DoesNotContain("Role: actor", prompt);
    }
}
