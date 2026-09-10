namespace ProjectBeacon.Application.Tests;

using Application.Reports;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ReportHandlerTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly Guid _projectId;

    public ReportHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("Beacon", null, org.Id);
        _db.Projects.Add(project);
        var todo = TaskItem.Create("Ready work", project.Id);
        var doing = TaskItem.Create("In flight", project.Id);
        doing.MoveToNextStatus();
        _db.Tasks.AddRange(todo, doing);
        _db.Milestones.Add(Milestone.Create("M1", null, project.Id, 0));
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Generate_ThenList_ReturnsSnapshot()
    {
        var generated = await new GenerateReportHandler(_db).HandleAsync(
            new GenerateReportCommand(new GenerateReportRequest(_projectId, "user", "tester")));
        Assert.True(generated.Success, generated.Error);
        Assert.Contains("Ready work", generated.Value.BodyMarkdown);
        Assert.Contains("In flight", generated.Value.BodyMarkdown);
        Assert.Contains("\"todo\":1", generated.Value.SnapshotJson, StringComparison.OrdinalIgnoreCase);

        var listed = await new ListReportsHandler(_db).HandleAsync(new ListReportsRequest(_projectId));
        Assert.True(listed.Success);
        Assert.Single(listed.Value);
        Assert.Equal(generated.Value.Id, listed.Value[0].Id);
    }
}
