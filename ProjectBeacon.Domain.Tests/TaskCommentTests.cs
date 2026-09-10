namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;

public sealed class TaskCommentTests
{
    [Fact]
    public void Create_SetsAllProperties()
    {
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var comment = TaskComment.Create("Great work!", taskId, userId);

        Assert.NotEqual(Guid.Empty, comment.Id);
        Assert.Equal("Great work!", comment.Content);
        Assert.Equal(taskId, comment.TaskId);
        Assert.Equal(userId, comment.UserId);
        Assert.Null(comment.UpdatedAt);
    }

    [Fact]
    public void Update_ChangesContent()
    {
        var comment = TaskComment.Create("Original", Guid.NewGuid(), Guid.NewGuid());
        comment.Update("Updated content");

        Assert.Equal("Updated content", comment.Content);
        Assert.NotNull(comment.UpdatedAt);
    }
}
