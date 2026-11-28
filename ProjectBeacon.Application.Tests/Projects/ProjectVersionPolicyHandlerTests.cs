namespace ProjectBeacon.Application.Tests.Projects;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectVersionPolicyHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;

    public ProjectVersionPolicyHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Version Policy Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Versioned Project", "Description", org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("version-owner", "version-owner@beacon.local", "hash");
        var member = User.Create("version-member", "version-member@beacon.local", "hash");
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

    private static ProjectVersionPolicy FourPartPolicy(bool releaseManagementEnabled = false) =>
        new("NumericComponents", 4, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("Directory.Build.props", "Xml", "//Version")],
            releaseManagementEnabled);

    [Fact]
    public async Task OwnerCanConfigureFourPartPolicyAndInitializeOfficialVersionOnce()
    {
        var handler = new SetProjectVersionPolicyHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService(), new EfAuditWriter());

        var result = await handler.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), "0.7.5.8", 1, OwnerActor()),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("0.7.5.8", result.Value!.OfficialVersion);
        Assert.Equal("NumericComponents", result.Value.Scheme);
        Assert.Equal(4, result.Value.ComponentCount);
        Assert.Equal(2, result.Value.Revision);
        Assert.Single(result.Value.Sources);
        Assert.Contains(await _db.AuditEvents.Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken), action => action == "project.version_policy.updated");
    }

    [Fact]
    public async Task MemberCannotChangePolicy_AndStaleRevisionIsRejected()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProjectVersionPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());

        var denied = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), "0.7.5.8", 1, MemberActor()),
            TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);

        var saved = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), "0.7.5.8", 1, OwnerActor()),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.Error);

        var stale = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), null, 1, OwnerActor()),
            TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);
    }

    [Fact]
    public async Task PolicyCannotChangeOfficialVersionAfterInitialization()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProjectVersionPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());
        var initialized = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), "0.7.5.8", 1, OwnerActor()),
            TestContext.Current.CancellationToken);
        Assert.True(initialized.Success, initialized.Error);

        var changed = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(), "0.7.5.9", initialized.Value!.Revision, OwnerActor()),
            TestContext.Current.CancellationToken);

        Assert.False(changed.Success);
        Assert.Equal(ErrorKind.Conflict, changed.Kind);
        // The handler writes through a separate unit-of-work context. Query with a fresh
        // context so this assertion verifies persisted state rather than the constructor's
        // still-tracked Project instance.
        await using var verifyDb = HandlerSqlite.Factory(_connection).CreateDbContext();
        var project = await verifyDb.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal("0.7.5.8", project.OfficialVersion);
    }

    [Fact]
    public async Task ReleaseManagementOptIn_IsProjectScopedAndDefaultsOff()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var setter = new SetProjectVersionPolicyHandler(factory, new AuthorizationService(), new EfAuditWriter());
        var getter = new GetProjectVersionPolicyHandler(factory, new AuthorizationService());

        var initial = await getter.HandleAsync(_projectId, OwnerActor(), TestContext.Current.CancellationToken);
        Assert.True(initial.Success, initial.Error);
        Assert.False(initial.Value!.ReleaseManagementEnabled);

        var enabled = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, FourPartPolicy(releaseManagementEnabled: true), null,
            initial.Value.Revision, OwnerActor()), TestContext.Current.CancellationToken);

        Assert.True(enabled.Success, enabled.Error);
        Assert.True(enabled.Value!.ReleaseManagementEnabled);

        var readBack = await getter.HandleAsync(_projectId, MemberActor(), TestContext.Current.CancellationToken);
        Assert.True(readBack.Success, readBack.Error);
        Assert.True(readBack.Value!.ReleaseManagementEnabled);

        var stored = await _db.Projects.Where(project => project.Id == _projectId)
            .Select(project => project.VersionPolicyJson)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"releaseManagementEnabled\":true", stored, StringComparison.Ordinal);

        // A fully published Release no longer has an active Prepare/Verify workflow.
        // Its historical Candidate evidence must not lock future project policy changes.
        await using (var stateDb = factory.CreateDbContext())
        {
            var project = await stateDb.Projects.SingleAsync(
                item => item.Id == _projectId, TestContext.Current.CancellationToken);
            project.InitializeOfficialVersion("0.7.5.8");

            var release = Domain.Entities.Roadmap.Release.Create(
                "Published release", null, _projectId, "0.7.5.8", "main", 1);
            release.TransitionTo(Domain.Enums.ReleaseStatus.InProgress);
            var deviceId = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            var prepareCommandId = Guid.NewGuid();
            var verifyCommandId = Guid.NewGuid();
            var publishCommandId = Guid.NewGuid();
            release.BeginPreparation(deviceId, candidateId, prepareCommandId, """{"version":"0.7.5.8"}""");
            release.RecordPrepareSuccess(prepareCommandId, """{"prepared":true}""");
            release.BeginVerification(deviceId, verifyCommandId);
            var candidateSha = new string('a', 40);
            var candidate = Domain.Entities.Roadmap.ReleaseCandidateSnapshot.Create(
                _projectId,
                release.Id,
                candidateId,
                deviceId,
                "0.7.5.8",
                "v0.7.5.8",
                "main",
                new string('1', 40),
                new string('2', 40),
                candidateSha,
                new string('3', 40),
                project.VersionPolicyRevision,
                release.Revision,
                new string('b', 64),
                """{"files":[],"checks":[]}""",
                DateTime.UtcNow);
            release.MarkCandidate(candidate);
            release.BeginPublication(deviceId, publishCommandId, """{"candidate":"immutable"}""");
            release.CompletePublication(
                publishCommandId,
                candidate.TagName,
                12345,
                "https://github.com/vitalyKl/ProjectBeacon/actions/runs/12345",
                candidate.Version,
                candidate.CandidateSha,
                DateTime.UtcNow,
                """{"tagPushed":true}""");
            stateDb.Releases.Add(release);
            stateDb.ReleaseCandidateSnapshots.Add(candidate);
            await stateDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var completedPolicy = FourPartPolicy(releaseManagementEnabled: false) with
        {
            TagPattern = "release-{version}"
        };
        var afterPublished = await setter.HandleAsync(new SetProjectVersionPolicyCommand(
            _projectId, completedPolicy, null, readBack.Value.Revision, OwnerActor()),
            TestContext.Current.CancellationToken);

        Assert.True(afterPublished.Success, afterPublished.Error);
        Assert.False(afterPublished.Value!.ReleaseManagementEnabled);
        Assert.Equal("0.7.5.8", afterPublished.Value.OfficialVersion);
    }

    [Fact]
    public async Task ProjectMemberCanReadPolicy_ButTokenMustBeBoundToExactProject()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var getter = new GetProjectVersionPolicyHandler(factory, new AuthorizationService());

        var human = await getter.HandleAsync(_projectId, MemberActor(), TestContext.Current.CancellationToken);
        Assert.True(human.Success, human.Error);

        var token = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null, _projectId, null, ApiTokenCapability.TaskRead);
        var tokenRead = await getter.HandleAsync(_projectId, token, TestContext.Current.CancellationToken);
        Assert.True(tokenRead.Success, tokenRead.Error);

        var wrongProjectToken = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null, Guid.NewGuid(), null, ApiTokenCapability.TaskRead);
        var wrongProjectRead = await getter.HandleAsync(_projectId, wrongProjectToken, TestContext.Current.CancellationToken);
        Assert.False(wrongProjectRead.Success);
        Assert.Equal(ErrorKind.Forbidden, wrongProjectRead.Kind);
    }
}
