namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Identity;
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
            new UpdateProjectRequest(projectId, "  ", "desc"), actor));
        Assert.False(blank.Success);

        var renamed = await handler.HandleAsync(new UpdateProjectCommand(
            new UpdateProjectRequest(projectId, " North ", "notes"), actor));
        Assert.True(renamed.Success, renamed.Error);
        Assert.Equal("North", renamed.Value!.Name);
        Assert.Equal("notes", renamed.Value.Description);
    }

    [Fact]
    public async Task Delete_RemovesProject()
    {
        var (actor, projectId) = await CreateOwnedProject();
        var deleted = await new DeleteProjectHandler(HandlerSqlite.Factory(_connection), _auth)
            .HandleAsync(new DeleteProjectCommand(projectId, actor));
        Assert.True(deleted.Success, deleted.Error);
        Assert.False(await _db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == projectId));
    }

    private async Task<(ActorContext Actor, Guid ProjectId)> CreateOwnedProject()
    {
        var user = User.Create("owner", "owner@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
            new CreateOrgCommand(new CreateOrgRequest("Acme", null), actor));
        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
            new CreateProjectCommand(new CreateProjectRequest("Beacon", "desc", org.Value!.Id), actor));
        return (actor, project.Value!.Id);
    }
}
