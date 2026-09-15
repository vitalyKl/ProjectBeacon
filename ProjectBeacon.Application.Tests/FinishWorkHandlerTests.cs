namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class FinishWorkHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _userId;

    public FinishWorkHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        var user = User.Create("admin", "admin@beacon.local", "hash", isAdmin: true);
        _db.Users.Add(user);
        _db.SaveChanges();
        _projectId = project.Id;
        _userId = user.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _userId, MemberRole.Admin));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Done_FromTodo_ReachesDone()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0))));

        Assert.True(result.Success, result.Error);
        await _db.Entry(task).ReloadAsync();
        Assert.Equal(TaskItemStatus.Done, task.Status);
    }
}
