namespace ProjectBeacon.Application.Tests.Milestones;

using Application.Milestones;
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

    public MilestoneLifecycleTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Milestone lifecycle org");
        _db.Orgs.Add(org);
        var project = Project.Create("Milestone lifecycle project", null, org.Id);
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
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id)),
            TestContext.Current.CancellationToken);

        Assert.True(closed.Success, closed.Error);
        Assert.Equal(2, closed.Value!.Revision);
        Assert.NotNull(closed.Value.ClosedAt);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var repeatedClose = await close.HandleAsync(
            new CloseMilestoneCommand(new CloseMilestoneRequest(milestone.Id)),
            TestContext.Current.CancellationToken);
        Assert.True(repeatedClose.Success, repeatedClose.Error);
        Assert.Equal(2, repeatedClose.Value!.Revision);
        Assert.Equal(2, await CountVersions(milestone.Id));

        var reopen = new ReopenMilestoneHandler(HandlerSqlite.Factory(_connection), writer);
        var reopened = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id)),
            TestContext.Current.CancellationToken);

        Assert.True(reopened.Success, reopened.Error);
        Assert.Equal(3, reopened.Value!.Revision);
        Assert.Null(reopened.Value.ClosedAt);
        Assert.Equal(3, await CountVersions(milestone.Id));

        var repeatedReopen = await reopen.HandleAsync(
            new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestone.Id)),
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

        using var closedSnapshot = System.Text.Json.JsonDocument.Parse(history[1].ContentJson);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null,
            closedSnapshot.RootElement.GetProperty("closedAt").ValueKind);
        using var reopenedSnapshot = System.Text.Json.JsonDocument.Parse(history[2].ContentJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            reopenedSnapshot.RootElement.GetProperty("closedAt").ValueKind);
    }

    private Task<int> CountVersions(Guid targetId) =>
        _db.ContentVersions.CountAsync(
            version => version.TargetKind == ContentTargetKind.Milestone
                && version.TargetId == targetId,
            TestContext.Current.CancellationToken);
}
