namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Projects;
using Tasks;

public sealed class ProjectCommandTests
{
    [Fact]
    public void CreateProjectCommand_CreatesFromRequest()
    {
        var request = new CreateProjectRequest("Test", "Desc", Guid.NewGuid());
        var command = new CreateProjectCommand(request, ActorContext.Anonymous);

        Assert.Equal(request.Name, command.Request.Name);
        Assert.Equal(request.Description, command.Request.Description);
        Assert.Equal(request.OrgId, command.Request.OrgId);
    }

    [Fact]
    public void UpdateProjectCommand_CreatesFromRequest()
    {
        var projectId = Guid.NewGuid();
        var request = new UpdateProjectRequest(projectId, "New Name", "New Desc");
        var command = new UpdateProjectCommand(request, ActorContext.Anonymous);

        Assert.Equal(projectId, command.Request.ProjectId);
        Assert.Equal("New Name", command.Request.Name);
    }
}
