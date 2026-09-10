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
    private readonly Guid _projectId;

    public CreateTaskAutoLabelTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
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
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateTask_WithPath_AssignsMatchingLabel()
    {
        var handler = new CreateTaskHandler(_db);
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
