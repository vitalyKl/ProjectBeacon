namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CreateTaskAutoLabelTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public CreateTaskAutoLabelTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        _db.Labels.Add(Label.Create("API", "#000", project.Id, "ProjectBeacon.API"));
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateTask_WithPath_AssignsMatchingLabel()
    {
        var handler = new CreateTaskHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CreateTaskCommand(new CreateTaskRequest(
            "Fix API",
            null,
            _projectId,
            TaskPriority.Medium,
            TaskType.Task,
            null,
            null,
            "ProjectBeacon.API/Program.cs")));
        Assert.True(result.Success, result.Error);
        var label = Assert.Single(_db.Labels);
        Assert.Equal(label.Id, result.Value.LabelId);
    }
}
