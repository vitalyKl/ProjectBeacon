namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Milestones;
using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

public sealed class TaskItemCommandTests
{
    [Fact]
    public void CreateTaskCommand_CreatesFromRequest()
    {
        var projectId = Guid.NewGuid();
        var labelId = Guid.NewGuid();
        var milestoneId = Guid.NewGuid();
        var request = new CreateTaskRequest(
            "Test Task", "Description", projectId,
            TaskPriority.High, TaskType.Bug, labelId, milestoneId);
        var command = new CreateTaskCommand(request);

        Assert.Equal("Test Task", command.Request.Title);
        Assert.Equal(projectId, command.Request.ProjectId);
        Assert.Equal(TaskPriority.High, command.Request.Priority);
        Assert.Equal(TaskType.Bug, command.Request.Type);
        Assert.Equal(labelId, command.Request.LabelId);
        Assert.Equal(milestoneId, command.Request.MilestoneId);
    }

    [Fact]
    public void UpdateTaskCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var request = new UpdateTaskRequest(taskId, "Updated", "New desc", TaskPriority.Critical, TaskType.Feature, null, null);
        var command = new UpdateTaskCommand(request);

        Assert.Equal(taskId, command.Request.TaskId);
        Assert.Equal("Updated", command.Request.Title);
        Assert.Equal(TaskPriority.Critical, command.Request.Priority);
    }

    [Fact]
    public void DeleteTaskCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var command = new DeleteTaskCommand(new DeleteTaskRequest(taskId));

        Assert.Equal(taskId, command.Request.TaskId);
    }

    [Fact]
    public void ChangeSubStageCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var command = new ChangeSubStageCommand(new ChangeSubStageRequest(taskId, TaskSubStage.Review));

        Assert.Equal(taskId, command.Request.TaskId);
        Assert.Equal(TaskSubStage.Review, command.Request.SubStage);
    }

    [Fact]
    public void AddCommentCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var command = new AddCommentCommand(new AddCommentRequest(taskId, "Great work!"));

        Assert.Equal(taskId, command.Request.TaskId);
        Assert.Equal("Great work!", command.Request.Content);
    }

    [Fact]
    public void SetDependenciesCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var depId1 = Guid.NewGuid();
        var depId2 = Guid.NewGuid();
        var command = new SetDependenciesCommand(new SetDependenciesRequest(taskId, new List<Guid> { depId1, depId2 }));

        Assert.Equal(taskId, command.Request.TaskId);
        Assert.Equal(2, command.Request.DependentTaskIds.Count);
    }

    [Fact]
    public void AddReviewNotesCommand_CreatesFromRequest()
    {
        var taskId = Guid.NewGuid();
        var command = new AddReviewNotesCommand(new AddReviewNotesRequest(taskId, "All good"));

        Assert.Equal(taskId, command.Request.TaskId);
        Assert.Equal("All good", command.Request.ReviewNotes);
    }
}

public sealed class MilestoneCommandTests
{
    [Fact]
    public void CreateMilestoneCommand_CreatesFromRequest()
    {
        var projectId = Guid.NewGuid();
        var request = new CreateMilestoneRequest("Milestone 1", "Desc", projectId, 1);
        var command = new CreateMilestoneCommand(request);

        Assert.Equal("Milestone 1", command.Request.Name);
        Assert.Equal(projectId, command.Request.ProjectId);
        Assert.Equal(1, command.Request.Order);
    }

    [Fact]
    public void UpdateMilestoneCommand_CreatesFromRequest()
    {
        var milestoneId = Guid.NewGuid();
        var request = new UpdateMilestoneRequest(milestoneId, "Updated", "New desc", 2);
        var command = new UpdateMilestoneCommand(request);

        Assert.Equal(milestoneId, command.Request.MilestoneId);
        Assert.Equal("Updated", command.Request.Name);
        Assert.Equal(2, command.Request.Order);
    }

    [Fact]
    public void DeleteMilestoneCommand_CreatesFromRequest()
    {
        var milestoneId = Guid.NewGuid();
        var command = new DeleteMilestoneCommand(new DeleteMilestoneRequest(milestoneId));

        Assert.Equal(milestoneId, command.Request.MilestoneId);
    }
}
