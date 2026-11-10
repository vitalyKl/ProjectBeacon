namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Entitlements;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Organizations;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

public sealed class OrganizationAdminServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly CapturingEmailSender _email = new();
    private readonly IConfiguration _configuration;
    private User _admin = null!;

    public OrganizationAdminServiceTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BEACON_PUBLIC_URL"] = "https://app.example.test",
                ["AUTH_LOCAL_INVITE_ONLY"] = "true"
            })
            .Build();
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }


    [Theory]
    [InlineData("")]
    [InlineData("https://control.projectbeacon.tech")]
    [InlineData("http://app.example.test")]
    public async Task CreateOrganization_RejectsMissingOrUnsafeCustomerInviteBaseUrl(string publicUrl)
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _seedDb.Users.Add(_admin);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Control:Host"] = "control.projectbeacon.tech",
                ["BEACON_PUBLIC_URL"] = publicUrl
            })
            .Build();
        var service = new EfOrganizationAdminService(
            _factory, _email, configuration, new EfAuditWriter(), TimeProvider.System);

        var result = await service.CreateAsync(actor,
            new CreateOrganizationWithOwnerRequest("Must not be created", null, "owner@example.test"));

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Unavailable, result.Kind);
        Assert.Empty(await _seedDb.Orgs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _seedDb.OrgInvites.IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task CreateOrganization_InvitesOwner_AndRegistrationAcceptsInvitation()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _seedDb.Users.Add(_admin);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var service = CreateService();
        var created = await service.CreateAsync(actor,
            new CreateOrganizationWithOwnerRequest("Acme", "Alpha customer", "owner@acme.example"));

        Assert.True(created.Success, created.Error);
        Assert.Equal("owner@acme.example", created.Value!.Email);
        Assert.StartsWith("https://app.example.test/invite?token=", created.Value.InviteUrl);
        Assert.Single(_email.Sent);

        var invite = await _seedDb.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Value.InviteId, TestContext.Current.CancellationToken);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.NotEqual(created.Value.InviteUrl.Split("token=")[1], invite.TokenHash);

        var registered = await new RegisterHandler(_factory, new BcryptPasswordHasher(), _configuration,
            auditWriter: new EfAuditWriter()).HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "acme-owner", "owner@acme.example", "a-strong-password", created.Value.InviteUrl.Split("token=")[1])),
            TestContext.Current.CancellationToken);

        Assert.True(registered.Success, registered.Error);
        Assert.True(await _seedDb.OrgMembers.IgnoreQueryFilters().AnyAsync(
            x => x.OrgId == created.Value.OrgId && x.UserId == registered.Value!.UserId && x.Role == MemberRole.Owner,
            TestContext.Current.CancellationToken));
        Assert.Equal(InviteStatus.Accepted, (await _seedDb.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Value.InviteId, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task OrganizationListAndDetailsExposeSupportMetadata_AndInviteCanBeRevoked()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _seedDb.Users.Add(_admin);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = CreateService();
        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var created = await service.CreateAsync(actor,
            new CreateOrganizationWithOwnerRequest("Northwind", null, "owner@northwind.example"));
        Assert.True(created.Success, created.Error);

        var page = await service.ListAsync(actor, "OWNER@NORTHWIND.EXAMPLE");
        Assert.True(page.Success, page.Error);
        var listed = Assert.Single(page.Value!.Items);
        Assert.Equal("owner@northwind.example", listed.OwnerEmail);
        Assert.Equal(1, listed.PendingInviteCount);

        var details = await service.GetAsync(actor, created.Value!.OrgId);
        Assert.True(details.Success, details.Error);
        Assert.Equal("Northwind", details.Value!.Organization.Name);
        Assert.Contains(details.Value.Invites, x => x.Id == created.Value.InviteId && x.Status == "Pending");

        var revoked = await service.RevokeInviteAsync(actor, created.Value.OrgId, created.Value.InviteId);
        Assert.True(revoked.Success, revoked.Error);
        Assert.Equal(InviteStatus.Revoked, (await _seedDb.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Value.InviteId, TestContext.Current.CancellationToken)).Status);
    }



    [Fact]
    public async Task InviteOwner_ExistingOrganization_CommitsInvitationBeforeSendingEmail()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _seedDb.Users.Add(_admin);
        var org = Org.Create("Existing customer");
        _seedDb.Orgs.Add(org);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var invited = await CreateService().InviteOwnerAsync(actor, org.Id, "OWNER@existing.example");

        Assert.True(invited.Success, invited.Error);
        Assert.Equal("owner@existing.example", invited.Value!.Email);
        Assert.StartsWith("https://app.example.test/invite?token=", invited.Value.InviteUrl);
        Assert.Single(_email.Sent);

        var persisted = await _seedDb.OrgInvites.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == invited.Value.InviteId, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(InviteStatus.Pending, persisted.Status);
        Assert.NotEqual(invited.Value.InviteUrl.Split("token=")[1], persisted.TokenHash);
    }

    [Fact]
    public async Task ExpiredOwnerInvitationsAreNotCountedAsPendingOrShownAsPending()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        _seedDb.Users.Add(_admin);
        var org = Org.Create("Expired invite customer");
        var invite = OrgInvite.Create(org.Id, "owner@expired.example", MemberRole.Owner,
            _admin.Id, "hashed-expired-token", DateTime.UtcNow.AddDays(-8));
        _seedDb.Orgs.Add(org);
        _seedDb.OrgInvites.Add(invite);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsRead);
        var service = CreateService();

        var page = await service.ListAsync(actor);
        Assert.True(page.Success, page.Error);
        var row = Assert.Single(page.Value!.Items);
        Assert.Equal(0, row.PendingInviteCount);
        Assert.Null(row.OwnerEmail);

        var details = await service.GetAsync(actor, org.Id);
        Assert.True(details.Success, details.Error);
        Assert.Equal("Expired", Assert.Single(details.Value!.Invites).Status);
    }

    [Fact]
    public async Task ChangeMemberRole_PreventsDemotingLastActiveOwner_AndAllowsOwnerHandoff()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var firstOwner = User.Create("first-owner", "first@example.test", "hash");
        var secondOwner = User.Create("second-owner", "second@example.test", "hash");
        var org = Org.Create("Role management customer");
        _seedDb.Users.AddRange(_admin, firstOwner, secondOwner);
        _seedDb.Orgs.Add(org);
        var firstMembership = OrgMember.Create(org.Id, firstOwner.Id, MemberRole.Owner);
        _seedDb.OrgMembers.Add(firstMembership);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var service = CreateService();

        var blocked = await service.ChangeMemberRoleAsync(actor, org.Id, firstOwner.Id, MemberRole.Admin);
        Assert.False(blocked.Success);
        Assert.Equal(ErrorKind.Conflict, blocked.Kind);
        Assert.Equal(MemberRole.Owner, firstMembership.Role);

        _seedDb.OrgMembers.Add(OrgMember.Create(org.Id, secondOwner.Id, MemberRole.Owner));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var changed = await service.ChangeMemberRoleAsync(actor, org.Id, firstOwner.Id, MemberRole.Admin);
        Assert.True(changed.Success, changed.Error);
        await _seedDb.Entry(firstMembership).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MemberRole.Admin, firstMembership.Role);
        Assert.True(await _seedDb.AuditEvents.IgnoreQueryFilters()
            .AnyAsync(x => x.Action == "platform.organization.member_role_changed",
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(OperatorRole.SystemAdministrator)]
    [InlineData(OperatorRole.Support)]
    [InlineData(OperatorRole.Billing)]
    [InlineData(OperatorRole.Auditor)]
    public async Task SetAccountDisabled_RejectsEveryPlatformOperatorRole(OperatorRole role)
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var target = User.Create("operator-target", "operator-target@example.test", "hash");
        target.AssignOperatorRole(role.ToString());
        var org = Org.Create("Operator account protection");
        _seedDb.Users.AddRange(_admin, target);
        _seedDb.Orgs.Add(org);
        _seedDb.OrgMembers.Add(OrgMember.Create(org.Id, target.Id, MemberRole.Member));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var cache = new CapturingSessionVersionCache();
        var service = CreateService(cache);

        var disabled = await service.SetAccountDisabledAsync(actor, org.Id, target.Id, disabled: true);

        Assert.False(disabled.Success);
        Assert.Equal(ErrorKind.Conflict, disabled.Kind);
        await _seedDb.Entry(target).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.False(target.IsDisabled);
        Assert.Equal(0, target.SessionVersion);
        Assert.Empty(cache.Invalidated);
        Assert.False(await _seedDb.AuditEvents.IgnoreQueryFilters().AnyAsync(
            item => item.TargetType == "User" && item.TargetId == target.Id.ToString()
                && item.Action == "platform.user.account_disabled",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetAccountDisabled_ProtectsLastOwnerAndInvalidatesSessionsOnDisableAndEnable()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var customerOwner = User.Create("customer-owner", "owner@customer.example", "hash");
        var org = Org.Create("Account access customer");
        var secondOrg = Org.Create("Second customer organization");
        _seedDb.Users.AddRange(_admin, customerOwner);
        _seedDb.Orgs.AddRange(org, secondOrg);
        _seedDb.OrgMembers.AddRange(
            OrgMember.Create(org.Id, customerOwner.Id, MemberRole.Owner),
            OrgMember.Create(secondOrg.Id, customerOwner.Id, MemberRole.Owner));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var cache = new CapturingSessionVersionCache();
        var service = CreateService(cache);

        var blocked = await service.SetAccountDisabledAsync(actor, org.Id, customerOwner.Id, disabled: true);
        Assert.False(blocked.Success);
        Assert.Equal(ErrorKind.Conflict, blocked.Kind);
        Assert.Null(customerOwner.DisabledAt);
        Assert.Equal(0, customerOwner.SessionVersion);
        Assert.Empty(cache.Invalidated);

        var firstBackupOwner = User.Create("backup-owner", "backup@customer.example", "hash");
        var secondBackupOwner = User.Create("second-backup", "second-backup@customer.example", "hash");
        _seedDb.Users.AddRange(firstBackupOwner, secondBackupOwner);
        _seedDb.OrgMembers.AddRange(
            OrgMember.Create(org.Id, firstBackupOwner.Id, MemberRole.Owner),
            OrgMember.Create(secondOrg.Id, secondBackupOwner.Id, MemberRole.Owner));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var disabled = await service.SetAccountDisabledAsync(actor, org.Id, customerOwner.Id, disabled: true);
        Assert.True(disabled.Success, disabled.Error);
        await _seedDb.Entry(customerOwner).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(customerOwner.DisabledAt);
        Assert.Equal(1, customerOwner.SessionVersion);
        Assert.Equal(new[] { customerOwner.Id }, cache.Invalidated);

        var enabled = await service.SetAccountDisabledAsync(actor, org.Id, customerOwner.Id, disabled: false);
        Assert.True(enabled.Success, enabled.Error);
        await _seedDb.Entry(customerOwner).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(customerOwner.DisabledAt);
        Assert.Equal(2, customerOwner.SessionVersion);
        Assert.Equal(new[] { customerOwner.Id, customerOwner.Id }, cache.Invalidated);

        Assert.Equal(2, await _seedDb.AuditEvents.IgnoreQueryFilters()
            .CountAsync(x => x.TargetType == "User" && x.TargetId == customerOwner.Id.ToString()
                && (x.Action == "platform.user.account_disabled" || x.Action == "platform.user.account_enabled"),
                TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task OrganizationLifecycle_RequiresSafeTransitionsAndAuditsEachChange()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var org = Org.Create("Lifecycle customer");
        _seedDb.Users.Add(_admin);
        _seedDb.Orgs.Add(org);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);
        var service = CreateService();

        var directClose = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Closed, "close without suspension");
        Assert.False(directClose.Success);
        Assert.Equal(ErrorKind.Conflict, directClose.Kind);

        var suspended = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Suspended, "customer requested pause");
        Assert.True(suspended.Success, suspended.Error);
        await _seedDb.Entry(org).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationLifecycleStatus.Suspended, org.LifecycleStatus);

        var invitation = await service.InviteOwnerAsync(actor, org.Id, "owner@paused.example");
        Assert.False(invitation.Success);
        Assert.Equal(ErrorKind.Conflict, invitation.Kind);

        var resumed = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Active, "access restored");
        Assert.True(resumed.Success, resumed.Error);
        await _seedDb.Entry(org).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationLifecycleStatus.Active, org.LifecycleStatus);

        var suspendedAgain = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Suspended, "closure preparation");
        Assert.True(suspendedAgain.Success, suspendedAgain.Error);
        var closed = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Closed, "customer contract terminated");
        Assert.True(closed.Success, closed.Error);
        await _seedDb.Entry(org).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationLifecycleStatus.Closed, org.LifecycleStatus);

        var reopen = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Active, "attempted reopen");
        Assert.False(reopen.Success);
        Assert.Equal(ErrorKind.Conflict, reopen.Kind);

        var actions = await _seedDb.AuditEvents.IgnoreQueryFilters()
            .Where(item => item.OrgId == org.Id)
            .Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, actions.Count(item => item == "platform.organization.suspended"));
        Assert.Single(actions, item => item == "platform.organization.reactivated");
        Assert.Single(actions, item => item == "platform.organization.closed");

        var reader = await AdminActorAsync(OperatorPermission.OrganizationsRead);
        var page = await service.ListAsync(reader);
        var listed = Assert.Single(page.Value!.Items);
        Assert.Equal(OrganizationLifecycleStatus.Closed, listed.LifecycleStatus);
        var details = await service.GetAsync(reader, org.Id);
        Assert.Equal(OrganizationLifecycleStatus.Closed, details.Value!.Organization.LifecycleStatus);
    }


    [Fact]
    public async Task SupportCanSuspendAndReactivateButCannotPermanentlyCloseOrganization()
    {
        var support = User.Create("support-operator", "support@example.test", "hash");
        support.AssignOperatorRole(OperatorRole.Support.ToString());
        support.BeginTotp("test-secret-cipher");
        support.ConfirmTotp();
        var org = Org.Create("Support-managed customer");
        _seedDb.Users.Add(support);
        _seedDb.Orgs.Add(org);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gate = new DatabaseAdminGate(_factory);
        var context = new ActorContext(ActorType.Human, support.Id, true, null, null, null, null,
            ApiTokenCapability.None, DateTimeOffset.UtcNow.AddMinutes(5));
        var actorResult = await gate.RequireAsync(
            context,
            OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage,
            requireElevation: true,
            ct: TestContext.Current.CancellationToken);
        Assert.True(actorResult.Success, actorResult.Error);
        var actor = actorResult.Value!;

        var service = CreateService();
        var suspended = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Suspended, "support hold");
        Assert.True(suspended.Success, suspended.Error);

        var closed = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Closed, "permanent closure");
        Assert.False(closed.Success);
        Assert.Equal(ErrorKind.Forbidden, closed.Kind);
        await _seedDb.Entry(org).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationLifecycleStatus.Suspended, org.LifecycleStatus);

        var resumed = await service.SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Active, "support restored access");
        Assert.True(resumed.Success, resumed.Error);
        await _seedDb.Entry(org).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationLifecycleStatus.Active, org.LifecycleStatus);
    }

    [Fact]
    public async Task OrganizationLifecycle_RejectsMissingReason()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var org = Org.Create("Invalid lifecycle customer");
        _seedDb.Users.Add(_admin);
        _seedDb.Orgs.Add(org);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = await AdminActorAsync(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);

        var missingReason = await CreateService().SetLifecycleStatusAsync(
            actor, org.Id, OrganizationLifecycleStatus.Suspended, "  ");
        Assert.False(missingReason.Success);
        Assert.Equal(ErrorKind.Validation, missingReason.Kind);
        Assert.Equal(OrganizationLifecycleStatus.Active, org.LifecycleStatus);
    }

    [Theory]
    [InlineData(OrganizationLifecycleStatus.Suspended, "organization_suspended")]
    [InlineData(OrganizationLifecycleStatus.Closed, "organization_closed")]
    public async Task EntitlementEvaluation_DeniesInactiveOrganizationBeforeGrantLookup(
        OrganizationLifecycleStatus status, string reasonCode)
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var org = Org.Create("Inactive entitlement customer");
        org.ChangeLifecycleStatus(OrganizationLifecycleStatus.Suspended, DateTime.UtcNow);
        if (status == OrganizationLifecycleStatus.Closed)
            org.ChangeLifecycleStatus(OrganizationLifecycleStatus.Closed, DateTime.UtcNow);
        _seedDb.Users.Add(_admin);
        _seedDb.Orgs.Add(org);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var entitlements = new ProjectBeacon.Infrastructure.Entitlements.PostgresEntitlementService(
            _factory, new EfAuditWriter(), TimeProvider.System);
        var decision = await entitlements.EvaluateAsync(
            org.Id, new EntitlementRequirement(EntitlementFeatureKeys.Core),
            TestContext.Current.CancellationToken);

        Assert.True(decision.Success, decision.Error);
        Assert.False(decision.Value!.Allowed);
        Assert.Equal(reasonCode, decision.Value.ReasonCode);
    }

    [Fact]
    public async Task SuspendedTenantFiltersHideOrgProjectAndOrgScopedRows_ButOperatorUnscopedReadsRemainAvailable()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var owner = User.Create("tenant-owner", "tenant-owner@example.test", "hash");
        var org = Org.Create("Suspended tenant");
        var project = Project.Create("Tenant project", null, org.Id);
        var membership = OrgMember.Create(org.Id, owner.Id, MemberRole.Owner);
        _seedDb.Users.AddRange(_admin, owner);
        _seedDb.Orgs.Add(org);
        _seedDb.Projects.Add(project);
        _seedDb.OrgMembers.Add(membership);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        org.ChangeLifecycleStatus(OrganizationLifecycleStatus.Suspended, DateTime.UtcNow);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        tenant.Assign(project.Id, org.Id, unscoped: false);
        tenant.SetLifecycleStatus(OrganizationLifecycleStatus.Suspended, enforced: true);
        await using var tenantDb = _factory.CreateDbContext(tenant);
        Assert.Empty(await tenantDb.Projects.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await tenantDb.Orgs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await tenantDb.OrgMembers.ToListAsync(TestContext.Current.CancellationToken));

        var operatorTenant = new TenantContext();
        operatorTenant.Assign(null, null, unscoped: true);
        await using var operatorDb = _factory.CreateDbContext(operatorTenant);
        Assert.Single(await operatorDb.Projects.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await operatorDb.Orgs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await operatorDb.OrgMembers.ToListAsync(TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task SuspendedTenantCanSeeAlternativeActiveOrganizationForSwitching()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var owner = User.Create("tenant-owner", "tenant-owner@example.test", "hash");
        var suspendedOrg = Org.Create("Suspended tenant");
        var activeOrg = Org.Create("Alternative active tenant");
        var project = Project.Create("Suspended project", null, suspendedOrg.Id);
        var activeProject = Project.Create("Active project", null, activeOrg.Id);
        _seedDb.Users.AddRange(_admin, owner);
        _seedDb.Orgs.AddRange(suspendedOrg, activeOrg);
        _seedDb.Projects.AddRange(project, activeProject);
        _seedDb.OrgMembers.AddRange(
            OrgMember.Create(suspendedOrg.Id, owner.Id, MemberRole.Owner),
            OrgMember.Create(activeOrg.Id, owner.Id, MemberRole.Member));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        suspendedOrg.ChangeLifecycleStatus(OrganizationLifecycleStatus.Suspended, DateTime.UtcNow);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        tenant.Assign(project.Id, suspendedOrg.Id, unscoped: false);
        tenant.SetLifecycleStatus(OrganizationLifecycleStatus.Suspended, enforced: true);
        await using var tenantDb = _factory.CreateDbContext(tenant);

        var availableOrganizations = await tenantDb.Orgs
            .OrderBy(org => org.Name)
            .Select(org => org.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { activeOrg.Id }, availableOrganizations);
        Assert.Empty(await tenantDb.Projects.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrganizationDetails_ExposeProjectAndSafeMemberDeviceInventory_RespectingUsersRead()
    {
        _admin = User.Create("platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var owner = User.Create("customer-owner", "owner@inventory.example", "hash");
        var org = Org.Create("Inventory customer", "Customer with projects and a workstation");
        var project = Project.Create("Customer portal", "Production application", org.Id);
        var device = DaemonDevice.Create(
            "Owner workstation", owner.Id, "private-fingerprint", "private-token-hash", "private-prefix");
        device.Heartbeat(instanceId: "private-instance-id");

        _seedDb.Users.AddRange(_admin, owner);
        _seedDb.Orgs.Add(org);
        _seedDb.Projects.Add(project);
        _seedDb.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        _seedDb.DaemonDevices.Add(device);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = CreateService();
        var reader = await AdminActorAsync(OperatorPermission.OrganizationsRead | OperatorPermission.UsersRead);
        var details = await service.GetAsync(reader, org.Id);

        Assert.True(details.Success, details.Error);
        Assert.Single(details.Value!.Projects);
        Assert.Equal(project.Id, details.Value.Projects[0].Id);
        Assert.Equal("Customer portal", details.Value.Projects[0].Name);
        Assert.Equal(1, details.Value.DeviceCount);
        var visibleDevice = Assert.Single(details.Value.Devices);
        Assert.Equal(device.Id, visibleDevice.Id);
        Assert.Equal("Owner workstation", visibleDevice.Name);
        Assert.Equal("customer-owner", visibleDevice.OwnerLogin);
        Assert.Equal("owner@inventory.example", visibleDevice.OwnerEmail);
        Assert.Equal("Online", visibleDevice.Status);
        Assert.NotNull(visibleDevice.LastHeartbeatAt);

        // The project inventory is operational metadata; personal workstation metadata is
        // limited to operators who also have UsersRead.
        var limitedReader = await AdminActorAsync(OperatorPermission.OrganizationsRead);
        var limitedDetails = await service.GetAsync(limitedReader, org.Id);
        Assert.True(limitedDetails.Success, limitedDetails.Error);
        Assert.Single(limitedDetails.Value!.Projects);
        Assert.Empty(limitedDetails.Value.Members);
        Assert.Empty(limitedDetails.Value.Devices);
        Assert.Equal(0, limitedDetails.Value.DeviceCount);
    }

    private EfOrganizationAdminService CreateService(IUserSessionVersionCache? sessionVersions = null) =>
        new(_factory, _email, _configuration, new EfAuditWriter(), TimeProvider.System, sessionVersions);

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

    private async Task<AdminActor> AdminActorAsync(OperatorPermission permission)
    {
        if (!_admin.TotpEnabled)
        {
            _admin.BeginTotp("test-secret-cipher");
            _admin.ConfirmTotp();
            await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var elevatedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        var gate = new DatabaseAdminGate(_factory);
        var result = await gate.RequireAsync(
            new ActorContext(ActorType.Human, _admin.Id, true, null, null, null, null,
                ApiTokenCapability.None, elevatedUntil),
            permission,
            requireElevation: true,
            ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }
}
