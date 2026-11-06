namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Tasks;
using ProjectBeacon.Domain.Enums;

public sealed class PipelineCancellationTests
{
    [Fact]
    public void CancelPipeline_MarksCancelledAndReturnsTaskToTodo()
    {
        var task = TaskItem.Create("cancel me", Guid.NewGuid());
        task.StartPipeline();

        task.CancelPipeline();

        Assert.Equal(TaskPipelineStage.Cancelled, task.PipelineStage);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.ReviewNotes);
        Assert.Equal(1, task.StateRevision);
    }

    [Fact]
    public void ResetPipeline_ClearsInitializedChainAndReturnsTaskToTodo()
    {
        var task = TaskItem.Create("restart me", Guid.NewGuid());
        task.StartPipeline();
        task.ResetPipeline();

        Assert.Null(task.PipelineStage);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.ReviewNotes);
        Assert.Equal(1, task.StateRevision);
    }

    [Fact]
    public void CancelPipeline_WhenNotStarted_Throws()
    {
        var task = TaskItem.Create("not started", Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => task.CancelPipeline());
    }

    [Theory]
    [InlineData(SubtaskStatus.InProgress)]
    [InlineData(SubtaskStatus.WaitingForUser)]
    public void CancelSubtask_ReturnsActiveWorkToPendingAndClearsResult(SubtaskStatus status)
    {
        var subtask = Subtask.Create("perform work", Guid.NewGuid(), Guid.NewGuid());
        subtask.Start();
        if (status == SubtaskStatus.WaitingForUser)
            subtask.WaitForUser();

        subtask.Cancel();

        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
        Assert.Null(subtask.DiffRef);
        Assert.Null(subtask.Summary);
        Assert.NotNull(subtask.UpdatedAt);
    }

    [Fact]
    public void CancelPendingSubtask_IsIdempotent()
    {
        var subtask = Subtask.Create("perform work", Guid.NewGuid(), Guid.NewGuid());

        subtask.Cancel();

        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
    }

    [Fact]
    public void CancelCompletedSubtask_IsRejected()
    {
        var subtask = Subtask.Create("perform work", Guid.NewGuid(), Guid.NewGuid());
        subtask.Start();
        subtask.ReportResult("diff:1", "done");

        Assert.Throws<InvalidOperationException>(() => subtask.Cancel());
        Assert.Equal(SubtaskStatus.Done, subtask.Status);
    }
}
