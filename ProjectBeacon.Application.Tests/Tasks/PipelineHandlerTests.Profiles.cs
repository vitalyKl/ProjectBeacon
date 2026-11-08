namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Common;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task ReviewReopen_QueueDispatcherStartsTheReopenedActorAutomatically()
    {
        var (projectId, task) = await SeedTaskAsync("Reopen and continue pipeline");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        var created = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Change behaviour")),
                TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
                task.Id, created.Value!.Id, string.Empty, "Initial implementation completed")),
                TestContext.Current.CancellationToken);
        Assert.True(reported.Success, reported.Error);

        var review = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(review.Success, review.Error);

        var verdict = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
                task.Id, ReviewVerdictKind.ReopenSubtask, "Please fix the reported edge case.", created.Value.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(verdict.Success, verdict.Error);
        Assert.Equal(TaskPipelineStage.ReopenedForRevision, verdict.Value!.Stage);

        var dispatcher = new PipelineActorQueueDispatcher(factory, spawner);
        await dispatcher.DispatchNextAsync(task.Id, review.Value!.Id, TestContext.Current.CancellationToken);

        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var pipelineTask = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            var reopened = await _db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken);
            var actor = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(session => session.TaskId == task.Id
                    && session.Role == PipelineRole.Actor, TestContext.Current.CancellationToken);

            Assert.Equal(TaskPipelineStage.Executing, pipelineTask.PipelineStage);
            Assert.Equal(SubtaskStatus.InProgress, reopened.Status);
            Assert.Equal(SessionStatus.Ready, actor.Status);
        }
    }

    [Fact]
    public async Task CreateSubtask_ProfileCannotExpandBaseToolsAndOptionalAttachmentIsExplicit()
    {
        var (projectId, task) = await SeedTaskAsync("Create profile-scoped subtasks");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var started = await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(started.Success, started.Error);

        var handler = new CreateSubtaskHandler(factory);
        var created = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Implement a feature", AllowedPaths: ["src/"],
            Profile: "implementer", OptionalMcpTools: [McpToolNames.TaskAttachFile])),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        Assert.Equal("implementer", created.Value!.ProfileKey);
        Assert.Contains(McpToolNames.WorkspaceWrite, created.Value.AllowedMcpTools);
        Assert.Contains(McpToolNames.TaskAttachFile, created.Value.AllowedMcpTools);
        Assert.Contains(McpToolNames.WriteFile, created.Value.AllowedMcpTools);

        var expansion = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Try to expand grants", AllowedMcpTools: [McpToolNames.TaskAttachFile],
            AllowedPaths: ["src/"], Profile: "implementer")),
            TestContext.Current.CancellationToken);
        Assert.False(expansion.Success);
        Assert.Contains("cannot expand", expansion.Error!, StringComparison.OrdinalIgnoreCase);

        var optionalExpansion = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Try an unknown optional grant", AllowedPaths: ["src/"],
            Profile: "implementer", OptionalMcpTools: [McpToolNames.PipelineStartReview])),
            TestContext.Current.CancellationToken);
        Assert.False(optionalExpansion.Success);
        Assert.Contains("optional grant", optionalExpansion.Error!, StringComparison.OrdinalIgnoreCase);

        var unknown = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            task.Id, "Unknown profile", Profile: "unknown")),
            TestContext.Current.CancellationToken);
        Assert.False(unknown.Success);
        Assert.Contains("Unknown subtask profile", unknown.Error);
    }
}
