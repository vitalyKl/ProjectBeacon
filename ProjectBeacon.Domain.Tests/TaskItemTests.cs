namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class TaskItemTests
{
    [Fact]
    public void Create_SetsDefaultValues()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Test Task", projectId);

        Assert.Equal("Test Task", task.Title);
        Assert.Equal(projectId, task.ProjectId);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal(TaskPriority.Medium, task.Priority);
        Assert.Equal(TaskType.Task, task.Type);
        Assert.Equal(TaskSubStage.RequirementGathering, task.SubStage);
        Assert.Null(task.LabelId);
        Assert.Null(task.MilestoneId);
        Assert.Null(task.ReviewNotes);
    }

    [Fact]
    public void Create_WithPriorityAndType_SetsValues()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Test Task", projectId, TaskPriority.High, TaskType.Bug);

        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(TaskType.Bug, task.Type);
    }

    [Fact]
    public void Update_ChangesTitle()
    {
        var task = TaskItem.Create("Original", Guid.NewGuid());
        task.Update(title: "Updated");

        Assert.Equal("Updated", task.Title);
    }

    [Fact]
    public void Update_ChangesDescription()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.Update(description: "New description");

        Assert.Equal("New description", task.Description);
    }

    [Fact]
    public void MoveToNextStatus_TodoToInProgress()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();

        Assert.Equal(TaskItemStatus.InProgress, task.Status);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void MoveToNextStatus_InProgressToDone()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();
        task.SetReviewNotes("Approved");
        task.MoveToNextStatus();

        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void MoveToNextStatus_DoneStaysDone()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus(); // Todo → InProgress
        task.SetReviewNotes("Approved");
        task.MoveToNextStatus(); // InProgress → Done
        task.MoveToNextStatus(); // Done → Done (no-op)

        Assert.Equal(TaskItemStatus.Done, task.Status);
    }

    [Fact]
    public void MoveToNextStatus_InProgressToDone_RequiresReviewNotes()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus(); // Todo → InProgress

        Assert.Throws<InvalidOperationException>(() => task.MoveToNextStatus());
    }

    [Fact]
    public void MoveToNextStatus_InProgressToDone_SucceedsWithReviewNotes()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus(); // Todo → InProgress
        task.SetReviewNotes("Reviewed and approved");

        task.MoveToNextStatus(); // InProgress → Done

        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void AssignLabel_SetsLabelId()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        var labelId = Guid.NewGuid();
        task.AssignLabel(labelId);

        Assert.Equal(labelId, task.LabelId);
    }

    [Fact]
    public void AssignLabel_ClearsLabelId()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        var labelId = Guid.NewGuid();
        task.AssignLabel(labelId);
        task.AssignLabel(null);

        Assert.Null(task.LabelId);
    }

    [Fact]
    public void SetMilestone_SetsMilestoneId()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        var milestoneId = Guid.NewGuid();
        task.SetMilestone(milestoneId);

        Assert.Equal(milestoneId, task.MilestoneId);
    }

    [Fact]
    public void SetMilestone_ClearsMilestoneId()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        var milestoneId = Guid.NewGuid();
        task.SetMilestone(milestoneId);
        task.SetMilestone(null);

        Assert.Null(task.MilestoneId);
    }

    [Fact]
    public void MoveToSubStage_ChangesSubStage()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();

        task.MoveToSubStage(TaskSubStage.Estimation);

        Assert.Equal(TaskSubStage.Estimation, task.SubStage);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
    }

    [Fact]
    public void MoveToSubStage_ToComplete_SetsDoneStatus()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();

        task.MoveToSubStage(TaskSubStage.Complete);

        Assert.Equal(TaskSubStage.Complete, task.SubStage);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void MoveToSubStage_ThrowsWhenNotInProgress()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => task.MoveToSubStage(TaskSubStage.Review));
    }

    [Fact]
    public void SetReviewNotes_SetsNotes()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.SetReviewNotes("Reviewed and approved");

        Assert.Equal("Reviewed and approved", task.ReviewNotes);
    }

    [Fact]
    public void SetReviewNotes_ThrowsOnEmpty()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => task.SetReviewNotes(""));
    }

    [Fact]
    public void SetReviewNotes_ThrowsOnWhitespace()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => task.SetReviewNotes("   "));
    }

    [Fact]
    public void TransitionTo_DoneToTodo_DoesNotHang()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();
        task.SetReviewNotes("ok");
        task.MoveToNextStatus();

        task.TransitionTo(TaskItemStatus.Todo);

        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void TransitionTo_DoneToInProgress()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.MoveToNextStatus();
        task.SetReviewNotes("ok");
        task.MoveToNextStatus();

        task.TransitionTo(TaskItemStatus.InProgress);

        Assert.Equal(TaskItemStatus.InProgress, task.Status);
    }

    [Fact]
    public void TransitionTo_TodoToDone_RequiresReviewNotes()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(TaskItemStatus.Done));
        Assert.Equal(TaskItemStatus.Todo, task.Status);
    }

    [Fact]
    public void TransitionTo_TodoToDone_WithNotes()
    {
        var task = TaskItem.Create("Test", Guid.NewGuid());
        task.SetReviewNotes("ok");
        task.TransitionTo(TaskItemStatus.Done);

        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.NotNull(task.CompletedAt);
    }
}
