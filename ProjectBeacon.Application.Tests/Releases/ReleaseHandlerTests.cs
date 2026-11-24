namespace ProjectBeacon.Application.Tests.Releases;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Projects;
using Application.Releases;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ReleaseHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;

    public ReleaseHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Release handlers org");
        _db.Orgs.Add(org);
        var project = Project.Create("Release handlers project", null, org.Id);
        project.SetVersionPolicyJson(ProjectVersionPolicyCatalog.Serialize(
            new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, [], true)));
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("release-owner", "release-owner@beacon.local", "hash");
        var member = User.Create("release-member", "release-member@beacon.local", "hash");
        _db.Users.AddRange(owner, member);
        _db.SaveChanges();
        _ownerId = owner.Id;
        _memberId = member.Id;
        _db.ProjectMembers.AddRange(
            ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner),
            ProjectMember.Create(_projectId, _memberId, MemberRole.Member));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private ActorContext OwnerActor() =>
        new(ActorType.Human, _ownerId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private ActorContext MemberActor() =>
        new(ActorType.Human, _memberId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private CreateReleaseHandler CreateHandler() =>
        new(Factory(), new AuthorizationService(), new EfAuditWriter());

    [Fact]
    public async Task OwnerCreatesRelease_MemberReads_AndMilestonesRemainAfterReleaseDeletion()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("0.7.5.9", "December delivery", "0.7.5.9", "release/0.7.5.9", 1),
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.Equal(ReleaseStatus.Planned, created.Value!.Status);
        Assert.Equal("0.7.5.9", created.Value.TargetVersion);

        var list = await new ListProjectReleasesHandler(Factory()).HandleAsync(
            new ListProjectReleasesCommand(new ListProjectReleasesRequest(_projectId), MemberActor()),
            TestContext.Current.CancellationToken);
        Assert.True(list.Success, list.Error);
        Assert.Single(list.Value!);

        var milestone = Milestone.Create("Prepare release notes", null, _projectId, 1);
        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var assign = await new SetMilestoneReleaseHandler(Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetMilestoneReleaseCommand(
                new SetMilestoneReleaseRequest(milestone.Id, _projectId, created.Value.Id, milestone.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(assign.Success, assign.Error);

        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(created.Value.Id, milestone.ReleaseId);

        var deleted = await new DeleteReleaseHandler(Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new DeleteReleaseCommand(
                new DeleteReleaseRequest(created.Value.Id, _projectId), OwnerActor()),
                TestContext.Current.CancellationToken);
        Assert.True(deleted.Success, deleted.Error);

        await _db.Entry(milestone).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(milestone.ReleaseId);
        Assert.True(await _db.Milestones.AnyAsync(
            item => item.Id == milestone.Id, TestContext.Current.CancellationToken));
        Assert.False(await _db.Releases.AnyAsync(
            item => item.Id == created.Value.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseCreationRequiresFeatureFlagAndOwnerAdmin()
    {
        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _projectId, null, ApiTokenCapability.None);
        var denied = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Denied", null, null, "main", 1),
            outsider), TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);

        var invalidVersion = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Invalid", null, "0.7.5", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(invalidVersion.Success);
        Assert.Equal(ErrorKind.Validation, invalidVersion.Kind);
    }

    [Fact]
    public async Task ReleaseCreationIsDisabledWhenProjectFeatureFlagIsOff()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.True(ProjectVersionPolicyCatalog.TryRead(
            project.VersionPolicyJson, out var policy, out var error), error);
        project.SetVersionPolicyJson(
            ProjectVersionPolicyCatalog.Serialize(policy with { ReleaseManagementEnabled = false }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Disabled", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.Releases.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlannedReleaseCanBeEditedAndOptionalFieldsCanBeCleared()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Initial name", "Release notes", "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var updated = await new UpdateReleaseHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new UpdateReleaseCommand(
                new UpdateReleaseRequest(
                    created.Value!.Id, _projectId, "Updated name", "", "", "release/0.7.5.9", 2,
                    created.Value.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        Assert.Equal("Updated name", updated.Value!.Name);
        Assert.Null(updated.Value.Description);
        Assert.Null(updated.Value.TargetVersion);
        Assert.Equal("release/0.7.5.9", updated.Value.TargetBranch);
        Assert.Equal(2, updated.Value.Order);
        Assert.Equal(created.Value.Revision + 1, updated.Value.Revision);
    }

    [Fact]
    public async Task ReleaseSnapshotComparisonIgnoresCaptureTimeButRejectsTaskChanges()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        var release = Release.Create("Snapshot", null, _projectId, "0.7.5.9", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var milestone = Milestone.Create("Included work", null, _projectId, 1);
        milestone.AssignRelease(release.Id);
        var task = TaskItem.Create(
            "Original task scope", _projectId, TaskPriority.High, TaskType.Feature, "Original description");
        task.SetMilestone(milestone.Id);
        _db.Releases.Add(release);
        _db.Milestones.Add(milestone);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(ProjectVersionPolicyCatalog.TryRead(
            project.VersionPolicyJson, out var policy, out var policyError), policyError);
        var prepared = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(prepared.Error);
        Assert.NotNull(prepared.Json);

        var unchanged = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(unchanged.Error);
        Assert.True(ReleasePreparePayload.MatchesSnapshot(prepared.Json, unchanged.Json));

        task.Update(title: "Changed after Prepare", description: "Changed release scope");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var changed = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(changed.Error);
        Assert.False(ReleasePreparePayload.MatchesSnapshot(prepared.Json, changed.Json));
    }

    [Fact]
    public async Task ReleaseSnapshotComparisonIgnoresCaptureTimeButRejectsTaskChanges()
    {
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        var release = Release.Create("Snapshot", null, _projectId, "0.7.5.9", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var milestone = Milestone.Create("Included work", null, _projectId, 1);
        milestone.AssignRelease(release.Id);
        var task = TaskItem.Create(
            "Original task scope", _projectId, TaskPriority.High, TaskType.Feature, "Original description");
        task.SetMilestone(milestone.Id);
        _db.Releases.Add(release);
        _db.Milestones.Add(milestone);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(ProjectVersionPolicyCatalog.TryRead(
            project.VersionPolicyJson, out var policy, out var policyError), policyError);
        var prepared = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(prepared.Error);
        Assert.NotNull(prepared.Json);

        var unchanged = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(unchanged.Error);
        Assert.True(ReleasePreparePayload.MatchesSnapshot(prepared.Json, unchanged.Json));

        task.Update(title: "Changed after Prepare", description: "Changed release scope");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var changed = await ReleasePreparePayload.BuildSnapshotAsync(
            _db, release, project, policy, TestContext.Current.CancellationToken);
        Assert.Null(changed.Error);
        Assert.False(ReleasePreparePayload.MatchesSnapshot(prepared.Json, changed.Json));
    }

    [Fact]
    public async Task ManualCandidateTransitionIsRejected()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Candidate must be prepared", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new SetReleaseStatusHandler(
                Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetReleaseStatusCommand(
                new SetReleaseStatusRequest(
                    created.Value!.Id, _projectId, ReleaseStatus.Candidate, created.Value.Revision),
                OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Validation, result.Kind);
        Assert.Equal(ReleaseStatus.Planned, (await _db.Releases
            .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task ReleaseManagementCannotBeDisabledWhileReleaseIsActive()
    {
        var created = await CreateHandler().HandleAsync(new CreateReleaseCommand(
            _projectId,
            new CreateReleaseRequest("Active", null, "0.7.5.9", "main", 1),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var policy = await _db.Projects.Where(project => project.Id == _projectId)
            .Select(project => new { project.VersionPolicyJson, project.VersionPolicyRevision })
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(ProjectVersionPolicyCatalog.TryRead(policy.VersionPolicyJson, out var parsed, out var parseError), parseError);

        var changed = await new SetProjectVersionPolicyHandler(
            Factory(), new AuthorizationService(), new EfAuditWriter())
            .HandleAsync(new SetProjectVersionPolicyCommand(
                _projectId, parsed with { ReleaseManagementEnabled = false },
                null, policy.VersionPolicyRevision, OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        var stillEnabled = await new GetProjectVersionPolicyHandler(Factory(), new AuthorizationService())
            .HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(stillEnabled.Success, stillEnabled.Error);
        Assert.True(stillEnabled.Value!.ReleaseManagementEnabled);
    }
}
