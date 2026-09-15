namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class ReviewVerdictTests
{
    [Fact]
    public void Create_SetsFields()
    {
        var taskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var verdict = ReviewVerdict.Create(taskId, projectId, ReviewVerdictKind.Approve, "All subtasks verified");

        Assert.Equal(taskId, verdict.TaskId);
        Assert.Equal(projectId, verdict.ProjectId);
        Assert.Equal(ReviewVerdictKind.Approve, verdict.Kind);
        Assert.Equal("All subtasks verified", verdict.Note);
        Assert.Null(verdict.SubtaskId);
        Assert.NotNull(verdict.CreatedAt);
    }

    [Fact]
    public void Create_WithSubtaskId()
    {
        var subtaskId = Guid.NewGuid();
        var verdict = ReviewVerdict.Create(
            taskId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            kind: ReviewVerdictKind.ReopenSubtask,
            note: "Missing edge case for empty input",
            subtaskId: subtaskId);

        Assert.Equal(subtaskId, verdict.SubtaskId);
        Assert.Equal(ReviewVerdictKind.ReopenSubtask, verdict.Kind);
        Assert.Equal("Missing edge case for empty input", verdict.Note);
    }

    [Fact]
    public void Create_EmptyNote_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ReviewVerdict.Create(Guid.NewGuid(), Guid.NewGuid(), ReviewVerdictKind.Approve, ""));

        Assert.Throws<ArgumentException>(() =>
            ReviewVerdict.Create(Guid.NewGuid(), Guid.NewGuid(), ReviewVerdictKind.ReopenSubtask, "   "));
    }
}
