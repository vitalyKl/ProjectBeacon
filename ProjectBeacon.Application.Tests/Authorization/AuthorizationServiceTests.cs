namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed partial class AuthorizationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly AuthorizationService _auth;

    public AuthorizationServiceTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _auth = new AuthorizationService();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private static ActorContext Worker() =>
        new(ActorType.Worker, null, false, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Device(Guid id) =>
        new(ActorType.Device, null, false, null, id, null, null, ApiTokenCapability.None);

    private static ActorContext Admin(Guid userId) =>
        new(ActorType.Human, userId, true, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Token(ApiTokenCapability caps = ApiTokenCapability.None, Guid? projectId = null) =>
        new(ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId, null, caps);

    [Fact]
    public async Task InactiveOrganizationBlocksProjectAndOrgAdminAccess_IncludingSystemAdmin()
    {
        var org = Org.Create("Suspended org");
        var project = Domain.Entities.Projects.Project.Create("Suspended project", null, org.Id);
        var owner = User.Create("owner", "owner@example.test", "hash");
        var systemAdmin = User.Create("system-admin", "admin@example.test", "hash", isAdmin: true);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.Users.AddRange(owner, systemAdmin);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        org.ChangeLifecycleStatus(OrganizationLifecycleStatus.Suspended, DateTime.UtcNow);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ownerProject = await _auth.CanAsync(_db, Human(owner.Id),
            ResourceType.Project, AuthAction.Administer, projectId: project.Id,
            ct: TestContext.Current.CancellationToken);
        Assert.False(ownerProject.Success);
        Assert.Equal(ErrorKind.Forbidden, ownerProject.Kind);

        var adminOrg = await _auth.CanAsync(_db, Admin(systemAdmin.Id),
            ResourceType.Org, AuthAction.Administer, orgId: org.Id,
            ct: TestContext.Current.CancellationToken);
        Assert.False(adminOrg.Success);
        Assert.Equal(ErrorKind.Forbidden, adminOrg.Kind);
    }



}


