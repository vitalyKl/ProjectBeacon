namespace ProjectBeacon.Application.Tests;

using Application.Identity;
using Application.Projects;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CreateProjectWizardTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;

    public CreateProjectWizardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Wizard_CreatesOrgProjectAndStarterLabels()
    {
        var org = await new CreateOrgHandler(_db).HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Acme", null)));
        Assert.True(org.Success, org.Error);

        var project = await new CreateProjectHandler(_db).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Beacon", "desc", org.Value!.Id)));
        Assert.True(project.Success, project.Error);
        Assert.Equal(org.Value.Id, project.Value!.OrgId);

        var labels = await new ListLabelsHandler(_db).HandleAsync(new ListLabelsRequest(project.Value.Id));
        Assert.True(labels.Success);
        Assert.Equal(5, labels.Value!.Count);
        Assert.Contains(labels.Value, l => l.Name == "API" && l.PathPrefix == "ProjectBeacon.API");
        Assert.Contains(labels.Value, l => l.Name == "Web");
        Assert.Contains(labels.Value, l => l.Name == "CLI");
    }
}
