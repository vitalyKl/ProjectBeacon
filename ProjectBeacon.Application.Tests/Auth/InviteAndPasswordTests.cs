namespace ProjectBeacon.Application.Tests;

using Application.Auth;
using Application.Common;
using Application.Authorization;
using Application.Entitlements;
using Application.Identity;
using Application.Persistence;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public sealed class InviteAndPasswordTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly CapturingEmailSender _mail = new();
    private readonly IConfiguration _open;
    private readonly IConfiguration _inviteOnly;
    private readonly IAuthorizationService _auth = new AuthorizationService();

    public InviteAndPasswordTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _open = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_LOCAL_INVITE_ONLY"] = "false",
            ["BEACON_PUBLIC_URL"] = "http://beacon.test"
        }).Build();
        _inviteOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_LOCAL_INVITE_ONLY"] = "true",
            ["BEACON_PUBLIC_URL"] = "http://beacon.test"
        }).Build();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private async Task<User> SeedAdminAsync()
    {
        var user = User.Create("admin", "admin@beacon.local", "hash", isAdmin: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private static ActorContext Human(Guid userId, bool isAdmin) =>
        new(ActorType.Human, userId, isAdmin, null, null, null, null, ApiTokenCapability.None);

    [Fact]
    public async Task Register_InviteOnly_WithoutToken_Fails()
    {
        var result = await new RegisterHandler(Factory(), new BcryptPasswordHasher(), _inviteOnly).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "bob@test.com", "password1")), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("Invite required.", result.Error);
    }

    [Fact]
    public async Task Register_WithProjectInvite_JoinsProjectAndOrg()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Acme");
        var project = Project.Create("P", null, org.Id);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invited = await new CreateProjectInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateProjectInviteRequest(project.Id, "bob@test.com", MemberRole.Member, Human(admin.Id, true)), TestContext.Current.CancellationToken);
        Assert.True(invited.Success, invited.Error);
        Assert.Contains("invite?token=", invited.Value!.Url);
        Assert.Single(_mail.Sent);

        var registered = await new RegisterHandler(Factory(), new BcryptPasswordHasher(), _inviteOnly).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "bob@test.com", "password1", invited.Value.Token)), TestContext.Current.CancellationToken);
        Assert.True(registered.Success, registered.Error);

        Assert.True(await _db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == project.Id && m.UserId == registered.Value!.UserId, TestContext.Current.CancellationToken));
        Assert.True(await _db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == org.Id && m.UserId == registered.Value!.UserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_InviteEmailMismatch_Fails()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invited = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, Human(admin.Id, true)), TestContext.Current.CancellationToken);
        Assert.True(invited.Success, invited.Error);

        var registered = await new RegisterHandler(Factory(), new BcryptPasswordHasher(), _open).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "other@test.com", "password1", invited.Value!.Token)), TestContext.Current.CancellationToken);
        Assert.False(registered.Success);
        Assert.Equal("Email does not match invite.", registered.Error);
        Assert.Equal(ErrorCodes.InviteEmailMismatch, registered.ErrorCode);
        Assert.Equal(ErrorKind.Forbidden, registered.Kind);
    }

    [Fact]
    public async Task ForgotPassword_DoesNotEnumerate_AndResetWorks()
    {
        var admin = await SeedAdminAsync();
        var unknown = await new ForgotPasswordHandler(Factory(), _mail, _open).HandleAsync(
            new ForgotPasswordRequest("nobody@test.com"), TestContext.Current.CancellationToken);
        Assert.True(unknown.Success);
        Assert.Empty(_mail.Sent);

        var forgot = await new ForgotPasswordHandler(Factory(), _mail, _open).HandleAsync(
            new ForgotPasswordRequest(admin.Email), TestContext.Current.CancellationToken);
        Assert.True(forgot.Success);
        Assert.Single(_mail.Sent);
        var body = _mail.Sent[0].Body;
        var token = body.Split("token=", 2)[1].Split('\n', 2)[0].Trim();

        var reset = await new ResetPasswordHandler(Factory(), new BcryptPasswordHasher()).HandleAsync(
            new ResetPasswordRequest(token, "newpass12"), TestContext.Current.CancellationToken);
        Assert.True(reset.Success, reset.Error);

        var login = await new LoginHandler(Factory(), new BcryptPasswordHasher(), new JwtTokenIssuer()).HandleAsync(
            new LoginCommand(new LoginRequest("admin", "newpass12")), TestContext.Current.CancellationToken);
        Assert.True(login.Success, login.Error);
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrent()
    {
        var hash = Infrastructure.Security.PasswordHasher.Hash("oldpass12");
        var user = User.Create("alice", "alice@test.com", hash);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bad = await new ChangePasswordHandler(Factory(), new BcryptPasswordHasher()).HandleAsync(
            new ChangePasswordRequest(user.Id, "wrong", "newpass12"), TestContext.Current.CancellationToken);
        Assert.False(bad.Success);

        var ok = await new ChangePasswordHandler(Factory(), new BcryptPasswordHasher()).HandleAsync(
            new ChangePasswordRequest(user.Id, "oldpass12", "newpass12"), TestContext.Current.CancellationToken);
        Assert.True(ok.Success, ok.Error);
    }

    [Fact]
    public async Task AcceptInvite_LoggedInUser()
    {
        var admin = await SeedAdminAsync();
        var bob = User.Create("bob", "bob@test.com", "hash");
        _db.Users.Add(bob);
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invited = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Admin, Human(admin.Id, true)), TestContext.Current.CancellationToken);
        Assert.True(invited.Success, invited.Error);

        var accepted = await new AcceptInviteHandler(Factory()).HandleAsync(
            new AcceptInviteRequest(invited.Value!.Token, Human(bob.Id, false)), TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        var member = await _db.OrgMembers.IgnoreQueryFilters()
            .SingleAsync(m => m.OrgId == org.Id && m.UserId == bob.Id, TestContext.Current.CancellationToken);
        Assert.Equal(MemberRole.Admin, member.Role);
    }

    [Fact]
    public async Task CreateOrgInvite_ReservesConfiguredMemberCapacity()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Seat limited org");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var evaluator = new MemberLimitEvaluator(2);
        var handler = new CreateOrgInviteHandler(
            Factory(), _mail, _auth, _open, entitlements: evaluator);

        var first = await handler.HandleAsync(
            new CreateOrgInviteRequest(org.Id, "first@test.com", MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        var second = await handler.HandleAsync(
            new CreateOrgInviteRequest(org.Id, "second@test.com", MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);

        Assert.False(second.Success);
        Assert.Equal("entitlement_limit_exceeded", second.ErrorCode);
        Assert.Equal(2L, evaluator.LastRequirement?.CurrentUsage);
        Assert.Equal(1, await _db.OrgInvites.IgnoreQueryFilters()
            .CountAsync(invite => invite.OrgId == org.Id && invite.Status == InviteStatus.Pending,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptOrgInvite_RechecksMemberCapacityAndLeavesInvitePendingWhenFull()
    {
        var admin = await SeedAdminAsync();
        var invitedUser = User.Create("member", "member@seat-limited.test", "hash");
        var org = Org.Create("Full org");
        _db.Users.Add(invitedUser);
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, invitedUser.Email, MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accepted = await new AcceptInviteHandler(
            Factory(), entitlements: new MemberLimitEvaluator(1)).HandleAsync(
                new AcceptInviteRequest(created.Value!.Token, Human(invitedUser.Id, false)),
                TestContext.Current.CancellationToken);

        Assert.False(accepted.Success);
        Assert.Equal("entitlement_limit_exceeded", accepted.ErrorCode);
        Assert.False(await _db.OrgMembers.IgnoreQueryFilters().AnyAsync(
            member => member.OrgId == org.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(InviteStatus.Pending, (await _db.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(invite => invite.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AcceptProjectInvite_CannotBypassOrganizationMemberCapacity()
    {
        var admin = await SeedAdminAsync();
        var invitedUser = User.Create("project-member", "project-member@seat-limited.test", "hash");
        var org = Org.Create("Project invite full org");
        var project = Project.Create("Project", null, org.Id);
        _db.Users.Add(invitedUser);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await new CreateProjectInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateProjectInviteRequest(project.Id, invitedUser.Email, MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var accepted = await new AcceptInviteHandler(
            Factory(), entitlements: new MemberLimitEvaluator(1)).HandleAsync(
                new AcceptInviteRequest(created.Value!.Token, Human(invitedUser.Id, false)),
                TestContext.Current.CancellationToken);

        Assert.False(accepted.Success);
        Assert.Equal("entitlement_limit_exceeded", accepted.ErrorCode);
        Assert.False(await _db.OrgMembers.IgnoreQueryFilters().AnyAsync(
            member => member.OrgId == org.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await _db.ProjectMembers.IgnoreQueryFilters().AnyAsync(
            member => member.ProjectId == project.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(InviteStatus.Pending, (await _db.ProjectInvites.IgnoreQueryFilters()
            .SingleAsync(invite => invite.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AcceptProjectInvite_ConsumesMatchingOrgInviteReservationOnce()
    {
        var admin = await SeedAdminAsync();
        var invitedUser = User.Create("dual-invite", "dual-invite@seat-limited.test", "hash");
        var org = Org.Create("Dual invite org");
        var project = Project.Create("Project", null, org.Id);
        _db.Users.Add(invitedUser);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var orgInvite = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, invitedUser.Email, MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(orgInvite.Success, orgInvite.Error);

        var projectInvite = await new CreateProjectInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateProjectInviteRequest(project.Id, invitedUser.Email, MemberRole.Member, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(projectInvite.Success, projectInvite.Error);

        var accepted = await new AcceptInviteHandler(
            Factory(), entitlements: new MemberLimitEvaluator(2)).HandleAsync(
                new AcceptInviteRequest(projectInvite.Value!.Token, Human(invitedUser.Id, false)),
                TestContext.Current.CancellationToken);

        Assert.True(accepted.Success, accepted.Error);
        Assert.True(await _db.OrgMembers.IgnoreQueryFilters().AnyAsync(
            member => member.OrgId == org.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken));
        Assert.True(await _db.ProjectMembers.IgnoreQueryFilters().AnyAsync(
            member => member.ProjectId == project.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(InviteStatus.Expired, (await _db.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(invite => invite.Id == orgInvite.Value!.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task SuspendedOrganizationInvite_CannotBePreviewedOrAccepted()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Suspended invite target");
        var owner = User.Create("invited-owner", "owner@suspended.example", "hash");
        _db.Orgs.Add(org);
        _db.Users.Add(owner);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, owner.Email, MemberRole.Owner, Human(admin.Id, true)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        org.ChangeLifecycleStatus(OrganizationLifecycleStatus.Suspended, DateTime.UtcNow);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var preview = await new GetInviteHandler(Factory()).HandleAsync(
            created.Value!.Token, TestContext.Current.CancellationToken);
        Assert.False(preview.Success);

        var accepted = await new AcceptInviteHandler(Factory()).HandleAsync(
            new AcceptInviteRequest(created.Value.Token, Human(owner.Id, false)),
            TestContext.Current.CancellationToken);
        Assert.False(accepted.Success);
        Assert.False(await _db.OrgMembers.IgnoreQueryFilters().AnyAsync(
            item => item.OrgId == org.Id && item.UserId == owner.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(InviteStatus.Pending, (await _db.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task RevokeOrgInvite_ReleasesPendingMemberReservation()
    {
        var owner = User.Create("owner-revoke", "owner-revoke@example.test", "hash");
        var org = Org.Create("Revoke invite organization");
        _db.Users.Add(owner);
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(
                org.Id, "reserved-seat@example.test", MemberRole.Member, Human(owner.Id, false)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var revoked = await new RevokeOrgInviteHandler(Factory(), _auth).HandleAsync(
            new RevokeInviteRequest(created.Value!.Id, Human(owner.Id, false)),
            TestContext.Current.CancellationToken);

        Assert.True(revoked.Success, revoked.Error);
        Assert.Equal(InviteStatus.Revoked, (await _db.OrgInvites.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task RevokeProjectInvite_SerializesWithMembershipAcceptance()
    {
        var owner = User.Create("project-invite-owner", "project-invite-owner@example.test", "hash");
        var org = Org.Create("Project invite revoke organization");
        var project = Project.Create("Project", null, org.Id);
        _db.Users.Add(owner);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var created = await new CreateProjectInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateProjectInviteRequest(
                project.Id, "project-reserved@example.test", MemberRole.Member, Human(owner.Id, false)),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);

        var revoked = await new RevokeProjectInviteHandler(Factory(), _auth).HandleAsync(
            new RevokeInviteRequest(created.Value!.Id, Human(owner.Id, false)),
            TestContext.Current.CancellationToken);

        Assert.True(revoked.Success, revoked.Error);
        Assert.Equal(InviteStatus.Revoked, (await _db.ProjectInvites.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == created.Value.Id, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task DuplicatePendingInvite_Fails()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, Human(admin.Id, true)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        var second = await new CreateOrgInviteHandler(Factory(), _mail, _auth, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, Human(admin.Id, true)), TestContext.Current.CancellationToken);
        Assert.False(second.Success);
        Assert.Equal("Invite already pending.", second.Error);
    }

    private sealed class MemberLimitEvaluator(long limit) : IEntitlementEvaluator
    {
        public EntitlementRequirement? LastRequirement { get; private set; }

        public Task<Result<EntitlementDecision>> EvaluateAsync(
            Guid orgId,
            EntitlementRequirement requirement,
            CancellationToken ct = default)
        {
            LastRequirement = requirement;
            var allowed = requirement.CurrentUsage <= limit
                && requirement.RequestedUnits <= limit - requirement.CurrentUsage;
            return Task.FromResult(Result.Ok(new EntitlementDecision(
                allowed,
                allowed ? null : "limit_exceeded",
                requirement.CurrentUsage,
                limit)));
        }
    }

}
