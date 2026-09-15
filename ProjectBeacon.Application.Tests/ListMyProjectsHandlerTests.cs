namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using ProjectBeacon.Application.Projects;

public sealed class ListMyProjectsHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public ListMyProjectsHandlerTests()
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
    public async Task Member_SeesOnlyMemberProjects_WithCountsAndRecentTasks()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var mine = Project.Create("Mine", null, org.Id);
        var other = Project.Create("Other", null, org.Id);
        _db.Projects.AddRange(mine, other);
        await _db.SaveChangesAsync();

        var alice = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(alice);
        await _db.SaveChangesAsync();

        _db.ProjectMembers.Add(ProjectMember.Create(mine.Id, alice.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var todo = TaskItem.Create("todo task", mine.Id);
        var inProgress = TaskItem.Create("progress task", mine.Id);
        inProgress.MoveToNextStatus();
        var inOther = TaskItem.Create("foreign task", other.Id);
        _db.Tasks.AddRange(todo, inProgress, inOther);
        await _db.SaveChangesAsync();

        var handler = new ListMyProjectsHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false);

        Assert.True(result.Success);
        var dto = result.Value;
        Assert.Equal(1, dto.TotalProjects);
        Assert.Equal(2, dto.TotalTasks);
        Assert.Equal(1, dto.InProgress);
        Assert.Equal(0, dto.Done);
        Assert.Single(dto.Projects);
        var summary = dto.Projects[0];
        Assert.Equal(mine.Id, summary.Id);
        Assert.Equal("Mine", summary.Name);
        Assert.Equal(2, summary.TotalTasks);
        Assert.Equal(1, summary.Todo);
        Assert.Equal(1, summary.InProgress);
        Assert.Equal(0, summary.Done);
        Assert.Equal(2, dto.RecentTasks.Count);
        Assert.All(dto.RecentTasks, t => Assert.Equal(mine.Id, t.ProjectId));
    }

    [Fact]
    public async Task Admin_SeesAllProjects_WithoutMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var a = Project.Create("A", null, org.Id);
        var b = Project.Create("B", null, org.Id);
        _db.Projects.AddRange(a, b);
        await _db.SaveChangesAsync();

        var root = User.Create("root", "root@example.com", "hash");
        _db.Users.Add(root);
        await _db.SaveChangesAsync();

        var handler = new ListMyProjectsHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(root.Id, isAdmin: true);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value.TotalProjects);
        var names = result.Value.Projects.Select(p => p.Name).ToList();
        Assert.Contains("A", names);
        Assert.Contains("B", names);
    }

    [Fact]
    public async Task UserWithoutMembership_ReturnsEmpty()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var bob = User.Create("bob", "bob@example.com", "hash");
        _db.Users.Add(bob);
        await _db.SaveChangesAsync();

        var handler = new ListMyProjectsHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(bob.Id, isAdmin: false);

        Assert.True(result.Success);
        Assert.Equal(0, result.Value.TotalProjects);
        Assert.Empty(result.Value.Projects);
        Assert.Empty(result.Value.RecentTasks);
    }

    [Fact]
    public async Task LastActivity_UsesCompletedAtWhenPresent()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var alice = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(alice);
        await _db.SaveChangesAsync();

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, alice.Id, MemberRole.Member));
        await _db.SaveChangesAsync();

        var done = TaskItem.Create("done task", project.Id);
        done.SetReviewNotes("looks good");
        done.TransitionTo(TaskItemStatus.Done);
        _db.Tasks.Add(done);
        await _db.SaveChangesAsync();

        var handler = new ListMyProjectsHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(alice.Id, isAdmin: false);

        Assert.True(result.Success);
        var summary = result.Value.Projects[0];
        Assert.True(summary.LastActivity >= done.CompletedAt!.Value);
        Assert.Single(result.Value.RecentTasks);
        Assert.Equal(done.CompletedAt, result.Value.RecentTasks[0].ActivityAt);
    }
}
