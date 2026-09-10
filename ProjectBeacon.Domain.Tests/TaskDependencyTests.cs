namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;

public sealed class TaskDependencyTests
{
    [Fact]
    public void Create_SetsTaskIds()
    {
        var taskId = Guid.NewGuid();
        var dependentTaskId = Guid.NewGuid();
        var dependency = TaskDependency.Create(taskId, dependentTaskId);

        Assert.NotEqual(Guid.Empty, dependency.Id);
        Assert.Equal(taskId, dependency.TaskId);
        Assert.Equal(dependentTaskId, dependency.DependentTaskId);
    }
}
