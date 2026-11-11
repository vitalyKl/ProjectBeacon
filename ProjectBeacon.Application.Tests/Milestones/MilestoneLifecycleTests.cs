namespace ProjectBeacon.Application.Tests.Milestones;

using Application.Milestones;
using Application.Common;
using Application.Authorization;
using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class MilestoneLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private Guid _ownerId;

    public MilestoneLifecycleTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Milestone lifecycle org");
        _db.Orgs.Add(org);
        var project = Project.Create("Milestone lifecycle project", null, org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("milestone-owner", "milestone-owner@beacon.local", "hash");
        _db.Users.Add(owner);
        _db.SaveChanges();
        _ownerId = owner.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task DeleteMilestone_ProjectPathMismatchDoesNotDeleteOrWriteAudit()
    {
        var milestone = Milestone.Create(
            "Protected milestone", "Must remain available.", _projectId, 1);
        _db.Milestones.Add(milestone);
        new ContentWriter().AddVersion(_db, milestone, "Human", _ownerId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var wrongProjectId = Guid.NewGuid();

        var result = await new DeleteMilestoneHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(
                new DeleteMilestoneCommand(
                    new DeleteMilestoneRequest(milestone.Id, wrongProjectId), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.NotFound, result.Kind);
        Assert.True(await _db.Milestones.AsNoTracking().AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(await _db.ContentVersions
            .Where(version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == milestone.Id)
            .Select(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken), revision => revision == 2);
        Assert.Empty(await _db.AuditEvents
            .Where(item => item.Action == "content.deleted"
                && item.TargetId == milestone.Id.ToString("D"))
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteMilestone_WritesTombstoneAndAuditAtomically()
    {
        var milestone = Milestone.Create(
            "Retired milestone", "Content to be removed.", _projectId, 2);
        var writer = new ContentWriter();
        _db.Milestones.Add(milestone);
        writer.AddVersion(_db, milestone, "Human", _ownerId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new DeleteMilestoneHandler(
                HandlerSqlite.Factory(_connection), writer, new Infrastructure.Audit.EfAuditWriter())
            .HandleAsync(
                new DeleteMilestoneCommand(new DeleteMilestoneRequest(milestone.Id), HumanActor()),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.False(await _db.Milestones.AsNoTracking().AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        var tombstone = await _db.ContentVersions.SingleAsync(
            version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == milestone.Id
                && version.Revision == 2,
            TestContext.Current.CancellationToken);
        using (var snapshot = System.Text.Json.JsonDocument.Parse(tombstone.ContentJson))
            Assert.True(snapshot.RootElement.GetProperty("deleted").GetBoolean());

        var audit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "content.deleted"
                && item.TargetType == nameof(ContentTargetKind.Milestone)
                && item.TargetId == milestone.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.Equal(_projectId, audit.ProjectId);
        Assert.Null(audit.DetailsJson);
    }

    [Fact]
    public async Task DeleteMilestone_RequiresProjectMembershipAndPreservesContent()
    {
        var milestone = Milestone.Create(
            "Protected milestone", "Protected roadmap content.", _projectId, 1);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", "owner");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var result = await new DeleteMilestoneHandler(HandlerSqlite.Factory(_connection), writer)
            .HandleAsync(
                new DeleteMilestoneCommand(new DeleteMilestoneRequest(milestone.Id), outsider),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.True(await _db.Milestones.AsNoTracking().AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.ContentVersions.CountAsync(
            version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == milestone.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await _db.AuditEvents.AnyAsync(
            item => item.Action == "content.deleted"
                && item.TargetId == milestone.Id.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateMilestone_RequiresProjectMembership()
    {
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var handler = new CreateMilestoneHandler(HandlerSqlite.Factory(_connection));
        var request = new CreateMilestoneRequest(
            "Unauthorized milestone", "Must not be created.", _projectId, 1);

        var denied = await handler.HandleAsync(
            new CreateMilestoneCommand(request, outsider),
            TestContext.Current.CancellationToken);

        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);
        Assert.Empty(await _db.Milestones.ToListAsync(TestContext.Current.CancellationToken));

        var allowed = await handler.HandleAsync(
            new CreateMilestoneCommand(
                request with { Name = "Owner milestone" }, HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal("Owner milestone", allowed.Value!.Name);
        Assert.Equal(1, await _db.ContentVersions.CountAsync(
            version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == allowed.Value.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMilestone_RequiresMembershipAndMatchingProjectScope()
    {
        var milestone = Milestone.Create("Original", "Original description", _projectId, 1);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", _ownerId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new UpdateMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var request = new UpdateMilestoneRequest(
            milestone.Id, "Changed", "Changed description", 2, milestone.Revision, _projectId);

        var denied = await handler.HandleAsync(
            new UpdateMilestoneCommand(request, outsider),
            TestContext.Current.CancellationToken);

        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);
        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Original", milestone.Name);
        Assert.Equal(1, await CountVersions(milestone.Id));

        var wrongProject = await handler.HandleAsync(
            new UpdateMilestoneCommand(
                request with { ProjectId = Guid.NewGuid() }, HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.False(wrongProject.Success);
        Assert.Equal(ErrorKind.NotFound, wrongProject.Kind);
        Assert.Equal("Original", milestone.Name);
        Assert.Equal(1, await CountVersions(milestone.Id));

        var allowed = await handler.HandleAsync(
            new UpdateMilestoneCommand(request, HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal("Changed", allowed.Value!.Name);
        Assert.Equal(2, allowed.Value.Revision);
        Assert.Equal(2, await CountVersions(milestone.Id));
    }

    [Fact]
    public async Task CloseAndReopen_RequireMembershipAndMatchingProjectScope()
    {
        var milestone = Milestone.Create("Scoped milestone", null, _projectId, 1);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", _ownerId.ToString("D"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var close = new CloseMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);

        var deniedClose = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id, _projectId), outsider),
            TestContext.Current.CancellationToken);
        Assert.False(deniedClose.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedClose.Kind);

        var wrongProjectClose = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id, Guid.NewGuid()), HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.False(wrongProjectClose.Success);
        Assert.Equal(ErrorKind.NotFound, wrongProjectClose.Kind);
        Assert.Equal(1, await CountVersions(milestone.Id));

        var closed = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(closed.Success, closed.Error);
        Assert.NotNull(closed.Value!.ClosedAt);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var reopen = new ReopenMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var deniedReopen = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id, _projectId), outsider),
            TestContext.Current.CancellationToken);
        Assert.False(deniedReopen.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedReopen.Kind);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var reopened = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(reopened.Success, reopened.Error);
        Assert.Null(reopened.Value!.ClosedAt);
        Assert.Equal(3, await CountVersions(milestone.Id));
    }

    [Fact]
    public async Task CloseAndReopen_AppendLifecycleVersions_AndRemainIdempotent()
    {
        var milestone = Milestone.Create(
            "Alpha milestone", "First release checkpoint", _projectId, 1);
        _db.Milestones.Add(milestone);
        var writer = new ContentWriter();
        writer.AddVersion(_db, milestone, "Human", "owner");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var close = new CloseMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var closed = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.True(closed.Success, closed.Error);
        Assert.Equal(2, closed.Value!.Revision);
        Assert.NotNull(closed.Value.ClosedAt);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var repeatedClose = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(repeatedClose.Success, repeatedClose.Error);
        Assert.Equal(2, repeatedClose.Value!.Revision);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var reopen = new ReopenMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var reopened = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);

        Assert.True(reopened.Success, reopened.Error);
        Assert.Equal(3, reopened.Value!.Revision);
        Assert.Null(reopened.Value.ClosedAt);
        Assert.Equal(3, await CountVersions(milestone.Id));

        var repeatedReopen = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id, _projectId), HumanActor()),
            TestContext.Current.CancellationToken);
        Assert.True(repeatedReopen.Success, repeatedReopen.Error);
        Assert.Equal(3, repeatedReopen.Value!.Revision);
        Assert.Equal(3, await CountVersions(milestone.Id));

        var history = await _db.ContentVersions
            .Where(version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == milestone.Id)
            .OrderBy(version => version.Revision)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2, 3 }, history.Select(version => version.Revision).ToArray());
        Assert.Equal("Human", history[1].ChangedByType);
        Assert.Equal(_ownerId.ToString("D"), history[1].ChangedById);
        Assert.Equal("Human", history[2].ChangedByType);
        Assert.Equal(_ownerId.ToString("D"), history[2].ChangedById);

        using var closedSnapshot = System.Text.Json.JsonDocument.Parse(history[1].ContentJson);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null,
            closedSnapshot.RootElement.GetProperty("closedAt").ValueKind);
        using var reopenedSnapshot = System.Text.Json.JsonDocument.Parse(history[2].ContentJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            reopenedSnapshot.RootElement.GetProperty("closedAt").ValueKind);
    }

    private ActorContext HumanActor() =>
        new(ActorType.Human, _ownerId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private Task<int> CountVersions(Guid targetId) =>
        _db.ContentVersions.CountAsync(
            version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == targetId,
            TestContext.Current.CancellationToken);
}
