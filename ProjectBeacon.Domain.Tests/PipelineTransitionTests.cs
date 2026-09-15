namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class PipelineTransitionTests
{
    private static TaskItem NewTask() => TaskItem.Create("Test", Guid.NewGuid());

    private static TaskItem TaskAtReviewing()
    {
        var task = NewTask();
        task.StartPipeline();
        task.EnterExecuting();
        task.EnterReview();
        return task;
    }

    [Fact]
    public void Create_PipelineStageIsNullByDefault()
    {
        var task = NewTask();

        Assert.Null(task.PipelineStage);
        Assert.Empty(task.Subtasks);
    }

    [Fact]
    public void RegularWorkflow_UnaffectedByPipeline()
    {
        var task = NewTask();
        task.MoveToNextStatus();
        task.SetReviewNotes("ok");
        task.MoveToNextStatus();

        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Null(task.PipelineStage);
    }

    [Fact]
    public void ColdDiffGate_StillAppliesWithoutPipeline()
    {
        var task = NewTask();

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(TaskItemStatus.Done));
    }

    [Fact]
    public void StartPipeline_SetsPlanningAndInProgress()
    {
        var task = NewTask();
        task.StartPipeline();

        Assert.Equal(TaskPipelineStage.Planning, task.PipelineStage);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
    }

    [Fact]
    public void StartPipeline_WhenAlreadyStarted_Throws()
    {
        var task = NewTask();
        task.StartPipeline();

        Assert.Throws<InvalidOperationException>(() => task.StartPipeline());
    }

    [Fact]
    public void EnterExecuting_FromPlanning_SetsExecuting()
    {
        var task = NewTask();
        task.StartPipeline();
        task.EnterExecuting();

        Assert.Equal(TaskPipelineStage.Executing, task.PipelineStage);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
    }

    [Fact]
    public void EnterExecuting_WhenAlreadyExecuting_IsIdempotent()
    {
        var task = NewTask();
        task.StartPipeline();
        task.EnterExecuting();

        task.EnterExecuting();

        Assert.Equal(TaskPipelineStage.Executing, task.PipelineStage);
    }

    [Fact]
    public void EnterExecuting_FromNone_Throws()
    {
        var task = NewTask();

        Assert.Throws<InvalidOperationException>(() => task.EnterExecuting());
    }

    [Fact]
    public void EnterExecuting_FromReviewing_Throws()
    {
        var task = TaskAtReviewing();

        Assert.Throws<InvalidOperationException>(() => task.EnterExecuting());
    }

    [Fact]
    public void EnterReview_FromExecuting_SetsReviewing()
    {
        var task = NewTask();
        task.StartPipeline();
        task.EnterExecuting();
        task.EnterReview();

        Assert.Equal(TaskPipelineStage.Reviewing, task.PipelineStage);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
    }

    [Fact]
    public void EnterReview_FromNone_Throws()
    {
        var task = NewTask();

        Assert.Throws<InvalidOperationException>(() => task.EnterReview());
    }

    [Fact]
    public void EnterReview_FromPlanning_Throws()
    {
        var task = NewTask();
        task.StartPipeline();

        Assert.Throws<InvalidOperationException>(() => task.EnterReview());
    }

    [Fact]
    public void SetApproved_FromReviewing_SetsApprovedAndDone()
    {
        var task = TaskAtReviewing();
        task.SetApproved();

        Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void SetApproved_FromExecuting_Throws()
    {
        var task = NewTask();
        task.StartPipeline();
        task.EnterExecuting();

        Assert.Throws<InvalidOperationException>(() => task.SetApproved());
    }

    [Fact]
    public void ReopenForRevision_FromApproved_ReturnsToInProgress()
    {
        var task = TaskAtReviewing();
        task.SetApproved();
        task.ReopenForRevision();

        Assert.Equal(TaskPipelineStage.ReopenedForRevision, task.PipelineStage);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void ReopenForRevision_FromReviewing_Throws()
    {
        var task = TaskAtReviewing();

        Assert.Throws<InvalidOperationException>(() => task.ReopenForRevision());
    }

    [Fact]
    public void ClosePipeline_FromApproved_SetsClosedWithNotes()
    {
        var task = TaskAtReviewing();
        task.SetApproved();
        task.ClosePipeline("All subtasks verified");

        Assert.Equal(TaskPipelineStage.Closed, task.PipelineStage);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Equal("All subtasks verified", task.ReviewNotes);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void ClosePipeline_FromReopenedForRevision_MovesToDone()
    {
        var task = TaskAtReviewing();
        task.SetApproved();
        task.ReopenForRevision();
        task.ClosePipeline("Revision accepted");

        Assert.Equal(TaskPipelineStage.Closed, task.PipelineStage);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Equal("Revision accepted", task.ReviewNotes);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void ClosePipeline_FromPlanning_Throws()
    {
        var task = NewTask();
        task.StartPipeline();

        Assert.Throws<InvalidOperationException>(() => task.ClosePipeline("notes"));
    }

    [Fact]
    public void ClosePipeline_FromReviewing_Throws()
    {
        var task = TaskAtReviewing();

        Assert.Throws<InvalidOperationException>(() => task.ClosePipeline("notes"));
    }

    [Fact]
    public void ClosePipeline_EmptyNotes_ThrowsAndKeepsState()
    {
        var task = TaskAtReviewing();
        task.SetApproved();

        Assert.Throws<ArgumentException>(() => task.ClosePipeline("  "));

        Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
        Assert.Null(task.ReviewNotes);
        Assert.Equal(TaskItemStatus.Done, task.Status);
    }
}
