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

        var uncheckedClose = await new ApprovePipelineHandler(factory)
            .HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(task.Id)));
        Assert.False(uncheckedClose.Success);

        await ProveReviewAsync(projectId, task.Id, factory, 0);

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
}
