namespace ProjectBeacon.Application.Tests.Admin;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class DatabaseAdminGateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly DatabaseAdminGate _gate;
    private readonly User _admin;
    private readonly User _member;

    public DatabaseAdminGateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        var factory = new BeaconDbFactory(options, tenant);
        _seedDb = new BeaconDbContext(options, tenant);
        _seedDb.Database.EnsureCreated();

        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _member = User.Create("member", "member@example.test", "hash");
        _seedDb.Users.AddRange(_admin, _member);
        _seedDb.SaveChanges();

        _gate = new DatabaseAdminGate(factory);
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RequireAsync_GrantsOnlyPersistedSystemAdministratorPermissions()
    {
        var result = await _gate.RequireAsync(Human(_admin.Id, isAdmin: true), OperatorPermission.AuditRead);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(_admin.Id, result.Value.UserId);
        Assert.True(result.Value.HasPermission(OperatorPermission.AuditRead));
        Assert.True(result.Value.HasPermission(OperatorPermission.SystemMaintenance));
        Assert.Null(result.Value.ElevatedUntilUtc);
    }

    [Fact]
    public async Task RequireAsync_UsesPersistedRolePermissionsInsteadOfGrantingEveryAdminFullAccess()
    {
        _admin.AssignOperatorRole(nameof(OperatorRole.Support));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var supportAccess = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true), OperatorPermission.OrganizationsManage);
        var deniedMaintenance = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true), OperatorPermission.SystemMaintenance);
        var deniedRoleManagement = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true), OperatorPermission.OperatorRolesManage);

        Assert.True(supportAccess.Success, supportAccess.Error);
        Assert.False(_admin.IsAdmin, "Support must not inherit the legacy customer-admin bypass.");
        Assert.Equal(nameof(OperatorRole.Support), _admin.OperatorRole);
        Assert.False(deniedMaintenance.Success);
        Assert.False(deniedRoleManagement.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedMaintenance.Kind);
    }

    [Theory]
    [InlineData(OperatorRole.Support, OperatorPermission.OrganizationsManage, OperatorPermission.BillingManage)]
    [InlineData(OperatorRole.Billing, OperatorPermission.BillingManage, OperatorPermission.UsersManage)]
    [InlineData(OperatorRole.Auditor, OperatorPermission.AuditExport, OperatorPermission.BillingManage)]
    public async Task RequireAsync_AdmitsLeastPrivilegeRolesWithoutLegacyAdminMarker(
        OperatorRole role, OperatorPermission allowed, OperatorPermission denied)
    {
        _admin.AssignOperatorRole(role.ToString());
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var access = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true), allowed,
            ct: TestContext.Current.CancellationToken);
        var deniedAccess = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true), denied,
            ct: TestContext.Current.CancellationToken);

        Assert.True(access.Success, access.Error);
        Assert.False(_admin.IsAdmin, "Least-privilege roles must not receive the legacy customer-admin marker.");
        Assert.Equal(role.ToString(), _admin.OperatorRole);
        Assert.False(deniedAccess.Success);
        Assert.Equal(ErrorKind.Forbidden, deniedAccess.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsApiTokenEvenWhenItClaimsAdminCapability()
    {
        var actor = new ActorContext(
            ActorType.ApiToken, _admin.Id, true, Guid.NewGuid(), null, null, null,
            ApiTokenCapability.Admin);

        var result = await _gate.RequireAsync(actor, OperatorPermission.UsersRead);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Theory]
    [InlineData(ActorType.Device)]
    [InlineData(ActorType.Worker)]
    public async Task RequireAsync_RejectsNonHumanActors(ActorType actorType)
    {
        var actor = new ActorContext(actorType, _admin.Id, true, null, Guid.NewGuid(), null, null,
            ApiTokenCapability.Admin);

        var result = await _gate.RequireAsync(actor, OperatorPermission.AuditRead);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsAnonymousActor()
    {
        var result = await _gate.RequireAsync(ActorContext.Anonymous, OperatorPermission.AuditRead);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsNonAdminAccountEvenWhenContextClaimsAdmin()
    {
        var result = await _gate.RequireAsync(Human(_member.Id, isAdmin: true), OperatorPermission.UsersRead);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsStaleContextAfterOperatorPrivilegeRemoval()
    {
        _admin.SetAdmin(false);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _gate.RequireAsync(Human(_admin.Id, isAdmin: true), OperatorPermission.UsersRead,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsDisabledOperator()
    {
        _admin.Disable();
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _gate.RequireAsync(Human(_admin.Id, isAdmin: true), OperatorPermission.UsersRead,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Unauthorized, result.Kind);
    }

    [Fact]
    public async Task RequireAsync_RejectsInvalidOrMissingPermission()
    {
        var noPermission = await _gate.RequireAsync(Human(_admin.Id, isAdmin: true), OperatorPermission.None);
        var unknownPermission = await _gate.RequireAsync(Human(_admin.Id, isAdmin: true), (OperatorPermission)(1UL << 40));

        Assert.False(noPermission.Success);
        Assert.Equal(ErrorKind.Validation, noPermission.Kind);
        Assert.False(unknownPermission.Success);
        Assert.Equal(ErrorKind.Validation, unknownPermission.Kind);
    }

    [Fact]
    public async Task RequireAsync_AllowsRecentStepUpOnlyWhenTotpIsPersistentlyEnabled()
    {
        _admin.BeginTotp("test-secret-cipher");
        _admin.ConfirmTotp();
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        var actor = Human(_admin.Id, isAdmin: true) with { OperatorElevatedUntilUtc = until };

        var result = await _gate.RequireAsync(actor, OperatorPermission.SystemMaintenance,
            requireElevation: true, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(until, result.Value!.ElevatedUntilUtc);
    }

    [Fact]
    public async Task RequireAsync_FailsClosedWhenElevationIsRequired()
    {
        var result = await _gate.RequireAsync(
            Human(_admin.Id, isAdmin: true),
            OperatorPermission.SystemMaintenance,
            requireElevation: true);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Contains("authenticator verification", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static ActorContext Human(Guid userId, bool isAdmin) =>
        new(ActorType.Human, userId, isAdmin, null, null, null, null, ApiTokenCapability.None);
}
