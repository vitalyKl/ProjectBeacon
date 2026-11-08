namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Identity;
using Application.Persistence;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectLifecycleTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly AuthorizationService _auth = new();

    public ProjectLifecycleTests()
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
    public async Task Rename_RejectsBlank_AndUpdatesName()
    {
        var (actor, projectId) = await CreateOwnedProject();
        var handler = new UpdateProjectHandler(HandlerSqlite.Factory(_connection), _auth);

        var blank = await handler.HandleAsync(new UpdateProjectCommand(
            new UpdateProjectRequest(projectId, "  ", "desc"), actor), TestContext.Current.CancellationToken);
        Assert.False(blank.Success);

        var renamed = await handler.HandleAsync(new UpdateProjectCommand(
            new UpdateProjectRequest(projectId, " North ", "notes"), actor), TestContext.Current.CancellationToken);
        Assert.True(renamed.Success, renamed.Error);
        Assert.Equal("North", renamed.Value!.Name);
        Assert.Equal("notes", renamed.Value.Description);
    }

    [Fact]
    public async Task Delete_RemovesProject()
    {
        var (actor, projectId) = await CreateOwnedProject();
        var deleted = await new DeleteProjectHandler(HandlerSqlite.Factory(_connection), _auth)
            .HandleAsync(new DeleteProjectCommand(projectId, actor), TestContext.Current.CancellationToken);
        Assert.True(deleted.Success, deleted.Error);
        Assert.False(await _db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == projectId, TestContext.Current.CancellationToken));
    }

    private async Task<Org> SeedOwnedOrgAsync(Guid userId, string name)
    {
        var org = Org.Create(name);
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, userId, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return org;
    }

    private async Task<(ActorContext Actor, Guid ProjectId)> CreateOwnedProject()
    {
        var user = User.Create("owner", "owner@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        var org = await SeedOwnedOrgAsync(user.Id, "Acme");
        var tenant = new TenantContext();
        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection), _auth, tenant).HandleAsync(
            new CreateProjectCommand(new CreateProjectRequest("Beacon", "desc", org.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(project.Success, project.Error);
        return (actor, project.Value!.Id);
    }
}
