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
