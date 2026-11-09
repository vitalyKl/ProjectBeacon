namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Identity;
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

    private EfOrganizationAdminService CreateService() =>
        new(_factory, _email, _configuration, new EfAuditWriter(), TimeProvider.System);

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
