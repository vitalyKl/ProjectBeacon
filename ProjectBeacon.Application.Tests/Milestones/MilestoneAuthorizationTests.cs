namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Milestones;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class MilestoneAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public MilestoneAuthorizationTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Milestone auth org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("Milestone auth project", null, org.Id);
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
    public async Task Manager_CanRunMilestoneLifecycle()
    {
        var actor = TestActorFactory.AdminFor(_projectId);
        var factory = HandlerSqlite.Factory(_connection);
        var create = await new CreateMilestoneHandler(factory).HandleAsync(
            new CreateMilestoneCommand(
                new CreateMilestoneRequest("Alpha", "Initial scope", _projectId, 1), actor),
            TestContext.Current.CancellationToken);
        Assert.True(create.Success, create.Error);

        var list = await new ListProjectMilestonesHandler(factory).HandleAsync(
            new ListProjectMilestonesCommand(new ListProjectMilestonesRequest(_projectId), actor),
            TestContext.Current.CancellationToken);
        Assert.True(list.Success, list.Error);
        Assert.Single(list.Value!);

        var get = await new GetMilestoneHandler(factory).HandleAsync(
            new GetMilestoneCommand(new GetMilestoneRequest(create.Value!.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(get.Success, get.Error);

        var update = await new UpdateMilestoneHandler(factory).HandleAsync(
            new UpdateMilestoneCommand(
                new UpdateMilestoneRequest(create.Value.Id, "Alpha 2", "Updated", 2), actor),
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.Error);
        Assert.Equal("Alpha 2", update.Value!.Name);

        var close = await new CloseMilestoneHandler(factory).HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(create.Value.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(close.Success, close.Error);
        Assert.NotNull(close.Value!.ClosedAt);

        var reopen = await new ReopenMilestoneHandler(factory).HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(create.Value.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(reopen.Success, reopen.Error);
        Assert.Null(reopen.Value!.ClosedAt);

        var delete = await new DeleteMilestoneHandler(factory).HandleAsync(
            new DeleteMilestoneCommand(new DeleteMilestoneRequest(create.Value.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(delete.Success, delete.Error);
        Assert.Empty(await _db.Milestones.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApiToken_CannotReadOrMutateMilestones()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var token = new ActorContext(
            ActorType.ApiToken,
            UserId: null,
            IsAdmin: false,
            TokenId: Guid.NewGuid(),
            DeviceId: null,
            ProjectId: _projectId,
            OrgId: null,
            Capabilities: ApiTokenCapability.TaskRead);

        var list = await new ListProjectMilestonesHandler(factory).HandleAsync(
            new ListProjectMilestonesCommand(new ListProjectMilestonesRequest(_projectId), token),
            TestContext.Current.CancellationToken);
        Assert.False(list.Success);
        Assert.Equal(ErrorKind.Forbidden, list.Kind);

        var create = await new CreateMilestoneHandler(factory).HandleAsync(
            new CreateMilestoneCommand(
                new CreateMilestoneRequest("Blocked", null, _projectId, 1), token),
            TestContext.Current.CancellationToken);
        Assert.False(create.Success);
        Assert.Equal(ErrorKind.Forbidden, create.Kind);
        Assert.Empty(await _db.Milestones.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HumanWithoutProjectMembership_CannotReadOrCreateMilestones()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var actor = new ActorContext(
            ActorType.Human,
            UserId: Guid.NewGuid(),
            IsAdmin: false,
            TokenId: null,
            DeviceId: null,
            ProjectId: null,
            OrgId: null,
            Capabilities: ApiTokenCapability.None);

        var list = await new ListProjectMilestonesHandler(factory).HandleAsync(
            new ListProjectMilestonesCommand(new ListProjectMilestonesRequest(_projectId), actor),
            TestContext.Current.CancellationToken);
        Assert.False(list.Success);
        Assert.Equal(ErrorKind.Forbidden, list.Kind);

        var create = await new CreateMilestoneHandler(factory).HandleAsync(
            new CreateMilestoneCommand(
                new CreateMilestoneRequest("Blocked", null, _projectId, 1), actor),
            TestContext.Current.CancellationToken);
        Assert.False(create.Success);
        Assert.Equal(ErrorKind.Forbidden, create.Kind);
    }
}
