namespace ProjectBeacon.Application.Tests;

using Application.Auth;
using Application.Identity;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
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

    private IDbContextFactory<BeaconDbContext> Factory() => HandlerSqlite.Factory(_connection);

    private async Task<User> SeedAdminAsync()
    {
        var user = User.Create("admin", "admin@beacon.local", "hash", isAdmin: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Register_InviteOnly_WithoutToken_Fails()
    {
        var result = await new RegisterHandler(Factory(), _inviteOnly).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "bob@test.com", "password1")));
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
        await _db.SaveChangesAsync();

        var invited = await new CreateProjectInviteHandler(Factory(), _mail, _open).HandleAsync(
            new CreateProjectInviteRequest(project.Id, "bob@test.com", MemberRole.Member, admin.Id, true));
        Assert.True(invited.Success, invited.Error);
        Assert.Contains("invite?token=", invited.Value!.Url);
        Assert.Single(_mail.Sent);

        var registered = await new RegisterHandler(Factory(), _inviteOnly).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "bob@test.com", "password1", invited.Value.Token)));
        Assert.True(registered.Success, registered.Error);

        Assert.True(await _db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == project.Id && m.UserId == registered.Value!.UserId));
        Assert.True(await _db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == org.Id && m.UserId == registered.Value!.UserId));
    }

    [Fact]
    public async Task Register_InviteEmailMismatch_Fails()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var invited = await new CreateOrgInviteHandler(Factory(), _mail, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, admin.Id, true));
        Assert.True(invited.Success, invited.Error);

        var registered = await new RegisterHandler(Factory(), _open).HandleAsync(
            new RegisterCommand(new RegisterRequest("bob", "other@test.com", "password1", invited.Value!.Token)));
        Assert.False(registered.Success);
        Assert.Equal("Email does not match invite.", registered.Error);
    }

    [Fact]
    public async Task ForgotPassword_DoesNotEnumerate_AndResetWorks()
    {
        var admin = await SeedAdminAsync();
        var unknown = await new ForgotPasswordHandler(Factory(), _mail, _open).HandleAsync(
            new ForgotPasswordRequest("nobody@test.com"));
        Assert.True(unknown.Success);
        Assert.Empty(_mail.Sent);

        var forgot = await new ForgotPasswordHandler(Factory(), _mail, _open).HandleAsync(
            new ForgotPasswordRequest(admin.Email));
        Assert.True(forgot.Success);
        Assert.Single(_mail.Sent);
        var body = _mail.Sent[0].Body;
        var token = body.Split("token=", 2)[1].Split('\n', 2)[0].Trim();

        var reset = await new ResetPasswordHandler(Factory()).HandleAsync(
            new ResetPasswordRequest(token, "newpass12"));
        Assert.True(reset.Success, reset.Error);

        var login = await new LoginHandler(Factory()).HandleAsync(
            new LoginCommand(new LoginRequest("admin", "newpass12")));
        Assert.True(login.Success, login.Error);
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrent()
    {
        var hash = Infrastructure.Security.PasswordHasher.Hash("oldpass12");
        var user = User.Create("alice", "alice@test.com", hash);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var bad = await new ChangePasswordHandler(Factory()).HandleAsync(
            new ChangePasswordRequest(user.Id, "wrong", "newpass12"));
        Assert.False(bad.Success);

        var ok = await new ChangePasswordHandler(Factory()).HandleAsync(
            new ChangePasswordRequest(user.Id, "oldpass12", "newpass12"));
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
        await _db.SaveChangesAsync();

        var invited = await new CreateOrgInviteHandler(Factory(), _mail, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Admin, admin.Id, true));
        Assert.True(invited.Success, invited.Error);

        var accepted = await new AcceptInviteHandler(Factory()).HandleAsync(
            new AcceptInviteRequest(invited.Value!.Token, bob.Id));
        Assert.True(accepted.Success, accepted.Error);
        var member = await _db.OrgMembers.IgnoreQueryFilters()
            .SingleAsync(m => m.OrgId == org.Id && m.UserId == bob.Id);
        Assert.Equal(MemberRole.Admin, member.Role);
    }

    [Fact]
    public async Task DuplicatePendingInvite_Fails()
    {
        var admin = await SeedAdminAsync();
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var first = await new CreateOrgInviteHandler(Factory(), _mail, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, admin.Id, true));
        Assert.True(first.Success, first.Error);
        var second = await new CreateOrgInviteHandler(Factory(), _mail, _open).HandleAsync(
            new CreateOrgInviteRequest(org.Id, "bob@test.com", MemberRole.Member, admin.Id, true));
        Assert.False(second.Success);
        Assert.Equal("Invite already pending.", second.Error);
    }
}
