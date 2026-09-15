namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ChangeTaskStatusHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public ChangeTaskStatusHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Org", null);
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
    public async Task DoneToTodo_DoesNotHang()
    {
        var task = TaskItem.Create("t", _projectId);
        task.SetReviewNotes("ok");
        task.TransitionTo(TaskItemStatus.Done);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var handler = new ChangeTaskStatusHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new ChangeTaskStatusCommand(
            new ChangeTaskStatusRequest(task.Id, TaskItemStatus.Todo)));

        Assert.True(result.Success, result.Error);
        Assert.Equal("Todo", result.Value!.Status);
    }

    [Fact]
    public async Task InProgressToDone_WithoutReviewNotes_FailsAndReloads()
    {
        var task = TaskItem.Create("t", _projectId);
        task.TransitionTo(TaskItemStatus.InProgress);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var handler = new ChangeTaskStatusHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new ChangeTaskStatusCommand(
            new ChangeTaskStatusRequest(task.Id, TaskItemStatus.Done)));

        Assert.False(result.Success);
        Assert.Contains("review notes", result.Error, StringComparison.OrdinalIgnoreCase);
        var stored = await _db.Tasks.FindAsync([task.Id]);
        Assert.Equal(TaskItemStatus.InProgress, stored!.Status);
    }
}
