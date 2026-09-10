namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;

public sealed class MilestoneTests
{
    [Fact]
    public void Create_SetsAllProperties()
    {
        var projectId = Guid.NewGuid();
        var milestone = Milestone.Create("Release 1.0", "First major release", projectId, 1);

        Assert.NotEqual(Guid.Empty, milestone.Id);
        Assert.Equal("Release 1.0", milestone.Name);
        Assert.Equal("First major release", milestone.Description);
        Assert.Equal(projectId, milestone.ProjectId);
        Assert.Equal(1, milestone.Order);
        Assert.Null(milestone.UpdatedAt);
    }

    [Fact]
    public void Update_ChangesName()
    {
        var milestone = Milestone.Create("Old", null, Guid.NewGuid(), 1);
        milestone.Update(name: "New");

        Assert.Equal("New", milestone.Name);
        Assert.NotNull(milestone.UpdatedAt);
    }

    [Fact]
    public void Update_ChangesDescription()
    {
        var milestone = Milestone.Create("Test", null, Guid.NewGuid(), 1);
        milestone.Update(description: "Updated desc");

        Assert.Equal("Updated desc", milestone.Description);
    }

    [Fact]
    public void Update_ChangesOrder()
    {
        var milestone = Milestone.Create("Test", null, Guid.NewGuid(), 1);
        milestone.Update(order: 5);

        Assert.Equal(5, milestone.Order);
    }

    [Fact]
    public void Close_SetsClosedAt_AndReopen_ClearsIt()
    {
        var milestone = Milestone.Create("M", null, Guid.NewGuid(), 1);
        Assert.Null(milestone.ClosedAt);
        milestone.Close();
        Assert.NotNull(milestone.ClosedAt);
        milestone.Reopen();
        Assert.Null(milestone.ClosedAt);
    }

    [Fact]
    public void Delete_SetsUpdatedAt()
    {
        var milestone = Milestone.Create("Test", null, Guid.NewGuid(), 1);
        milestone.Delete();

        Assert.NotNull(milestone.UpdatedAt);
    }
}
