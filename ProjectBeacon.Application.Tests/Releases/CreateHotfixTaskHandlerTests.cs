namespace ProjectBeacon.Application.Tests.Releases;

using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Releases;
using Domain.Common;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CreateHotfixTaskHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;
    private readonly Guid _deviceId;

    public CreateHotfixTaskHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Hotfix tests");
        var owner = User.Create("hotfix-owner", "hotfix-owner@beacon.local", "hash");
        var member = User.Create("hotfix-member", "hotfix-member@beacon.local", "hash");
        var project = Project.Create("Hotfix project", null, org.Id);
        project.SetVersionPolicyJson(ProjectVersionPolicyCatalog.Serialize(
            new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, [], true)));

        _db.Orgs.Add(org);
        _db.Users.AddRange(owner, member);
        _db.Projects.Add(project);
        _db.SaveChanges();

        _projectId = project.Id;
        _ownerId = owner.Id;
        _memberId = member.Id;

        _db.ProjectMembers.AddRange(
            ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner),
            ProjectMember.Create(_projectId, _memberId, MemberRole.Member));

        var device = DaemonDevice.Create(
            "hotfix-workstation", _ownerId, "hotfix-fingerprint", "hotfix-token-hash", "bcd_hotfix");
        device.Heartbeat(
            """{"capabilities":["git-land-v1"]}""",
            """{"workspaceMode":"Branch"}""");
        _deviceId = device.Id;
        _db.DaemonDevices.Add(device);
        _db.ProjectRuntimes.Add(ProjectRuntime.Create(
            _projectId, _deviceId, "/workspace/hotfix-project"));
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

    private CreateHotfixTaskHandler CreateHandler() =>
        new(Factory(), new AuthorizationService(), new EfAuditWriter());

    private Release CreatePublishedRelease(
        string name,
        string version,
        string deployedSha,
        DateTime deployedAtUtc)
    {
        var release = Release.Create(
            name, "Verified production release", _projectId, version, "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);

        var candidateId = Guid.NewGuid();
        var prepareCommandId = Guid.NewGuid();
        var verifyCommandId = Guid.NewGuid();
        var publishCommandId = Guid.NewGuid();

        release.BeginPreparation(_deviceId, candidateId, prepareCommandId, """{"version":"candidate"}""");
        release.RecordPrepareSuccess(prepareCommandId, """{"status":"prepared"}""");
        release.BeginVerification(_deviceId, verifyCommandId);

        var candidateSha = deployedSha.ToLowerInvariant();
        var candidate = ReleaseCandidateSnapshot.Create(
            _projectId,
            release.Id,
            candidateId,
            _deviceId,
            version,
            "v" + version,
            "main",
            new string('1', 40),
            new string('2', 40),
            candidateSha,
            new string('3', 40),
            1,
            release.Revision,
            new string('a', 64),
            """{"files":[],"checks":[]}""",
            deployedAtUtc.AddMinutes(-2));
        release.MarkCandidate(candidate);

        release.BeginPublication(_deviceId, publishCommandId, """{"candidateId":"immutable"}""");
        release.CompletePublication(
            publishCommandId,
            candidate.TagName,
            12345,
            "https://github.com/vitalyKl/ProjectBeacon/actions/runs/12345",
            version,
            candidateSha,
            deployedAtUtc,
            """{"tagPushed":true,"deploymentVerified":true}""");
        return release;
    }

    [Fact]
    public async Task OwnerCreatesHotfixFromLatestPublishedDeployment_AndRequestIsIdempotent()
    {
        var deployedAt = DateTime.UtcNow.AddMinutes(-1);
        var deployedSha = new string('a', 40);
        var release = CreatePublishedRelease("Current production", "0.7.5.8", deployedSha, deployedAt);
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var requestId = Guid.NewGuid();
        var request = new CreateHotfixTaskRequest(
            "Fix production regression",
            "Correct the failing production response without changing the public API contract.",
            requestId);
        var handler = CreateHandler();

        var created = await handler.HandleAsync(
            new CreateHotfixTaskCommand(_projectId, release.Id, request, OwnerActor()),
            TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        Assert.Equal(release.Id, created.Value!.HotfixSourceReleaseId);
        Assert.Equal(deployedSha, created.Value.HotfixDeployedSha);
        Assert.Equal("main", created.Value.HotfixTargetBranch);
        Assert.Equal(requestId, created.Value.HotfixRequestId);

        var stored = await _db.Tasks.SingleAsync(
            task => task.Id == created.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(_deviceId, stored.PipelineDeviceId);
        Assert.Equal(release.Id, stored.HotfixSourceReleaseId);

        var retry = await handler.HandleAsync(
            new CreateHotfixTaskCommand(_projectId, release.Id, request, OwnerActor()),
            TestContext.Current.CancellationToken);

        Assert.True(retry.Success, retry.Error);
        Assert.Equal(created.Value.Id, retry.Value!.Id);
        Assert.Equal(1, await _db.Tasks.CountAsync(
            task => task.HotfixRequestId == requestId, TestContext.Current.CancellationToken));

        var audit = await _db.AuditEvents.SingleAsync(
            item => item.Action == "project.release.hotfix_task_created",
            TestContext.Current.CancellationToken);
        Assert.Equal(created.Value.Id.ToString("D"), audit.TargetId);
    }

    [Fact]
    public async Task MemberCannotCreateHotfixTask()
    {
        var release = CreatePublishedRelease(
            "Current production", "0.7.5.8", new string('a', 40), DateTime.UtcNow.AddMinutes(-1));
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler().HandleAsync(new CreateHotfixTaskCommand(
            _projectId,
            release.Id,
            new CreateHotfixTaskRequest("Unauthorized Hotfix", null, Guid.NewGuid()),
            MemberActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Empty(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HotfixCannotStartFromAnOlderReleaseAfterANewerProductionDeployment()
    {
        var older = CreatePublishedRelease(
            "Earlier production", "0.7.5.7", new string('a', 40), DateTime.UtcNow.AddHours(-2));
        var current = CreatePublishedRelease(
            "Current production", "0.7.5.8", new string('b', 40), DateTime.UtcNow.AddMinutes(-1));
        _db.Releases.AddRange(older, current);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler().HandleAsync(new CreateHotfixTaskCommand(
            _projectId,
            older.Id,
            new CreateHotfixTaskRequest("Stale Hotfix", null, Guid.NewGuid()),
            OwnerActor()), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        Assert.Contains("current production", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeviceBootstrapReturnsOnlyPublishedDeployedShaForPinnedHotfix()
    {
        var deployedSha = new string('c', 40);
        var release = CreatePublishedRelease(
            "Current production", "0.7.5.8", deployedSha, DateTime.UtcNow.AddMinutes(-1));
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await CreateHandler().HandleAsync(new CreateHotfixTaskCommand(
            _projectId,
            release.Id,
            new CreateHotfixTaskRequest("Bootstrap Hotfix", "Fix production", Guid.NewGuid()),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var result = await new GetDeviceTaskWorkspaceBootstrapHandler(Factory()).HandleAsync(
            _deviceId, created.Value!.Id, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.IsHotfix);
        Assert.Equal(release.Id, result.Value.SourceReleaseId);
        Assert.Equal("main", result.Value.BaseBranch);
        Assert.Equal(deployedSha, result.Value.BaseSha);
    }

    [Fact]
    public async Task DeviceBootstrapFailsClosedWhenAnotherReleaseHasBeenDeployedSinceHotfixCreation()
    {
        var oldSha = new string('d', 40);
        var oldRelease = CreatePublishedRelease(
            "Previous production", "0.7.5.7", oldSha, DateTime.UtcNow.AddHours(-1));
        _db.Releases.Add(oldRelease);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hotfix = await CreateHandler().HandleAsync(new CreateHotfixTaskCommand(
            _projectId,
            oldRelease.Id,
            new CreateHotfixTaskRequest("Old base Hotfix", null, Guid.NewGuid()),
            OwnerActor()), TestContext.Current.CancellationToken);
        Assert.False(hotfix.Success); // A stale Release is rejected before a Hotfix can be pinned.
        Assert.Contains("current production", hotfix.Error, StringComparison.OrdinalIgnoreCase);

        var newest = CreatePublishedRelease(
            "Newest production", "0.7.5.8", new string('e', 40), DateTime.UtcNow);
        _db.Releases.Add(newest);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await _db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
    }
}
