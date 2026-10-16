namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using ProjectBeacon.Application.Projects;

public sealed class GetProjectOverviewHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public GetProjectOverviewHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Member_SeesProjectOverview_WithCountsAndRecentTasks()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("Mine", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var alice = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(alice);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, alice.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var todo = TaskItem.Create("todo", project.Id);
        var inProgress = TaskItem.Create("progress", project.Id);
        inProgress.MoveToNextStatus();
        _db.Tasks.AddRange(todo, inProgress);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new GetProjectOverviewHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false, project.Id, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        var dto = result.Value!;
        Assert.Equal(project.Id, dto.Id);
        Assert.Equal("Mine", dto.Name);
        Assert.Equal(2, dto.TotalTasks);
        Assert.Equal(1, dto.Todo);
        Assert.Equal(1, dto.InProgress);
        Assert.Equal(0, dto.Done);
        Assert.Equal(2, dto.RecentTasks.Count);
        Assert.All(dto.RecentTasks, t => Assert.Equal(project.Id, t.ProjectId));
    }

    [Fact]
    public async Task Admin_SeesProjectOverview_WithoutMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var root = User.Create("root", "root@example.com", "hash");
        _db.Users.Add(root);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new GetProjectOverviewHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(root.Id, isAdmin: true, project.Id, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(project.Id, result.Value!.Id);
    }

    [Fact]
    public async Task NonMember_DoesNotSeeProjectOverview()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bob = User.Create("bob", "bob@example.com", "hash");
        _db.Users.Add(bob);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new GetProjectOverviewHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(bob.Id, isAdmin: false, project.Id, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task UnknownProject_ReturnsNull()
    {
        var root = User.Create("root", "root@example.com", "hash");
        _db.Users.Add(root);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new GetProjectOverviewHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(root.Id, isAdmin: true, Guid.NewGuid(), ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }
}
