namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Projects;

public sealed class LabelHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public LabelHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
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
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Create_Update_Delete_Label()
    {
        var create = new CreateLabelHandler(HandlerSqlite.Factory(_connection));
        var created = await create.HandleAsync(new CreateLabelRequest(_projectId, "API", null, "ProjectBeacon.API"));
        Assert.True(created.Success, created.Error);
        Assert.Equal("API", created.Value!.Name);
        Assert.Equal("ProjectBeacon.API", created.Value.PathPrefix);

        var update = new UpdateLabelHandler(HandlerSqlite.Factory(_connection));
        var updated = await update.HandleAsync(new UpdateLabelRequest(
            _projectId, created.Value.Id, "Web", "#111111", "ProjectBeacon.Web"));
        Assert.True(updated.Success, updated.Error);
        Assert.Equal("Web", updated.Value!.Name);
        Assert.Equal("#111111", updated.Value.Color);
        Assert.Equal("ProjectBeacon.Web", updated.Value.PathPrefix);

        var task = TaskItem.Create("t", _projectId);
        task.AssignLabel(created.Value.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var delete = new DeleteLabelHandler(HandlerSqlite.Factory(_connection));
        var deleted = await delete.HandleAsync(new DeleteLabelRequest(_projectId, created.Value.Id));
        Assert.True(deleted.Success, deleted.Error);

        await _db.Entry(task).ReloadAsync();
        Assert.Null(task.LabelId);
        Assert.False(await _db.Labels.AsNoTracking().AnyAsync(l => l.Id == created.Value.Id));
    }

    [Fact]
    public async Task Create_EmptyName_Fails()
    {
        var handler = new CreateLabelHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new CreateLabelRequest(_projectId, "  ", null, null));
        Assert.False(result.Success);
    }
}
