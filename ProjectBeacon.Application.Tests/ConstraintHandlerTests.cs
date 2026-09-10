namespace ProjectBeacon.Application.Tests;

using Application.Context;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ConstraintHandlerTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly Guid _projectId;

    public ConstraintHandlerTests()
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
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Activate_ThenReject_UpdatesStatus()
    {
        var created = await new CreateConstraintHandler(_db).HandleAsync(
            new CreateConstraintRequest(_projectId, "must ship tests", ConstraintKind.Must));
        Assert.True(created.Success);
        Assert.Equal(ConstraintStatus.Proposed, created.Value.Status);

        var activated = await new ActivateConstraintHandler(_db).HandleAsync(created.Value.Id);
        Assert.True(activated.Success);
        Assert.Equal(ConstraintStatus.Active, activated.Value.Status);

        var rejected = await new RejectConstraintHandler(_db).HandleAsync(created.Value.Id);
        Assert.True(rejected.Success);
        Assert.Equal(ConstraintStatus.Rejected, rejected.Value.Status);
    }
}
