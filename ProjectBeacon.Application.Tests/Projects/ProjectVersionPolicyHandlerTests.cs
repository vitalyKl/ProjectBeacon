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

    private static ProjectVersionPolicy FourPartPolicy() =>
        new("NumericComponents", 4, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("Directory.Build.props", "Xml", "//Version")]);

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
        var project = await _db.Projects.SingleAsync(
            item => item.Id == _projectId, TestContext.Current.CancellationToken);
        Assert.Equal("0.7.5.8", project.OfficialVersion);
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
