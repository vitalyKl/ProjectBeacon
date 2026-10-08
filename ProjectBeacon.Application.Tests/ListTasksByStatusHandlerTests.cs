namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed class ListTasksByStatusHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ListTasksByStatusHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ListsMatchingStatus_CaseInsensitive()
    {
        Guid projectId;
        Guid runningId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            var running = TaskItem.Create("Running", project.Id);
            running.TransitionTo(TaskItemStatus.InProgress);
            var todo = TaskItem.Create("Waiting", project.Id);
            _db.Tasks.AddRange(running, todo);
            await _db.SaveChangesAsync();
            projectId = project.Id;
            runningId = running.Id;
        }

        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var result = await new ListTasksByStatusHandler(HandlerSqlite.Factory(_connection, tenant))
            .HandleAsync(new ListTasksByStatusCommand(new ListTasksByStatusRequest(projectId, "inprogress")));

        Assert.True(result.Success, result.Error);
        var match = Assert.Single(result.Value!);
        Assert.Equal(runningId, match.Id);
    }
}
