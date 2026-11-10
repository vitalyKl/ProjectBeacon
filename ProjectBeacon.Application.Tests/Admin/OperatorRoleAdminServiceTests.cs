namespace ProjectBeacon.Application.Tests.Admin;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Application.Tests;

public sealed class OperatorRoleAdminServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly User _admin;
    private readonly User _candidate;
    private readonly CapturingSessionVersionCache _sessionVersions = new();
    private readonly OperatorRoleAdminService _service;

    public OperatorRoleAdminServiceTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);
        _admin = User.Create("system-admin", "system-admin@example.test", "hash", isAdmin: true);
        _admin.BeginTotp("test-secret-cipher");
        _admin.ConfirmTotp();
        _candidate = User.Create("support-user", "support@example.test", "hash");
        _seedDb.Users.AddRange(_admin, _candidate);
        _seedDb.SaveChanges();
        _service = new OperatorRoleAdminService(_factory, new EfAuditWriter(), TimeProvider.System, _sessionVersions);
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AssignAndRemoveRole_ChangesEffectivePermissionsAndInvalidatesSessions()
    {
        var actor = await SystemAdminAsync();
        var assigned = await _service.AssignAsync(actor, _candidate.Id, OperatorRole.Support);
        Assert.True(assigned.Success, assigned.Error);

        await _seedDb.Entry(_candidate).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(_candidate.IsAdmin, "Support must not inherit the legacy customer-admin bypass.");
        Assert.Equal(nameof(OperatorRole.Support), _candidate.OperatorRole);
        Assert.Equal(1, _candidate.SessionVersion);
        Assert.Equal(new[] { _candidate.Id }, _sessionVersions.Invalidated);

        var gate = new DatabaseAdminGate(_factory);
        var supportControlContext = Human(_candidate.Id, isAdmin: true);
        var supportCustomerContext = Human(_candidate.Id, isAdmin: false);
        var supportAllowed = await gate.RequireAsync(supportControlContext, OperatorPermission.OrganizationsManage);
        var supportDenied = await gate.RequireAsync(supportControlContext, OperatorPermission.AuditExport);
        var customerSessionDenied = await gate.RequireAsync(supportCustomerContext, OperatorPermission.OrganizationsRead);
        Assert.True(supportAllowed.Success, supportAllowed.Error);
        Assert.False(supportDenied.Success);
        Assert.False(customerSessionDenied.Success);

        var audit = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "platform.operator_role.assigned",
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, audit.ActorUserId);
        Assert.Equal(_candidate.Id.ToString(), audit.TargetId);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);

        var removed = await _service.RemoveAsync(actor, _candidate.Id);
        Assert.True(removed.Success, removed.Error);
        await _seedDb.Entry(_candidate).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(_candidate.IsAdmin);
        Assert.Null(_candidate.OperatorRole);
        Assert.Equal(2, _candidate.SessionVersion);
        Assert.Equal(new[] { _candidate.Id, _candidate.Id }, _sessionVersions.Invalidated);

        var revoked = await gate.RequireAsync(supportControlContext, OperatorPermission.OrganizationsRead);
        Assert.False(revoked.Success);
    }

    [Fact]
    public async Task RoleMutations_RequireRecentStepUp_AndDisallowSelfRoleChanges()
    {
        var actor = await SystemAdminAsync();
        var withoutElevation = await new DatabaseAdminGate(_factory).RequireAsync(
            Human(_admin.Id, isAdmin: true), OperatorPermission.OperatorRolesManage);
        Assert.True(withoutElevation.Success, withoutElevation.Error);

        var noElevation = await _service.AssignAsync(withoutElevation.Value!, _candidate.Id, OperatorRole.Auditor);
        var selfChange = await _service.AssignAsync(actor, _admin.Id, OperatorRole.Support);

        Assert.False(noElevation.Success);
        Assert.Equal(ErrorKind.Forbidden, noElevation.Kind);
        Assert.False(selfChange.Success);
        Assert.Equal(ErrorKind.Forbidden, selfChange.Kind);
        Assert.False(_candidate.IsAdmin);
    }

    [Fact]
    public async Task RoleInventory_IncludesCustomerAccountsButNeverSecretFields()
    {
        var actor = await SystemAdminAsync();
        var result = await _service.ListAsync(actor);
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Value!, item => item.UserId == _candidate.Id
            && item.OperatorRole is null && item.Email == _candidate.Email);
        Assert.DoesNotContain("test-secret-cipher", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    private sealed class CapturingSessionVersionCache : IUserSessionVersionCache
    {
        public List<Guid> Invalidated { get; } = [];

        public bool TryGet(Guid userId, out UserSessionVersionSnapshot? snapshot)
        {
            snapshot = null;
            return false;
        }

        public void Set(Guid userId, UserSessionVersionSnapshot snapshot) { }

        public void Invalidate(Guid userId) => Invalidated.Add(userId);
    }

    private async Task<AdminActor> SystemAdminAsync()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        var result = await new DatabaseAdminGate(_factory).RequireAsync(
            Human(_admin.Id, isAdmin: true) with { OperatorElevatedUntilUtc = until },
            OperatorPermission.OperatorRolesManage,
            requireElevation: true,
            ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    private static ActorContext Human(Guid userId, bool isAdmin) =>
        new(ActorType.Human, userId, isAdmin, null, null, null, null, ApiTokenCapability.None);
}
