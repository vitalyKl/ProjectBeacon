namespace ProjectBeacon.Application.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Projects;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;

public sealed class AuditInstrumentationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly IAuditWriter _auditWriter = new EfAuditWriter();
    private readonly IAuthorizationService _authorization = new AuthorizationService();
    private readonly User _admin;
    private readonly User _invitee;
    private readonly Org _org;
    private readonly Project _project;

    public AuditInstrumentationTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);

        _admin = User.Create("audit-admin", "audit-admin@example.test", "hash", isAdmin: true);
        _invitee = User.Create("audit-invitee", "audit-invitee@example.test", "hash");
        _org = Org.Create("Audit Org");
        _project = Project.Create("Audit Project", null, _org.Id);

        _seedDb.Users.AddRange(_admin, _invitee);
        _seedDb.Orgs.Add(_org);
        _seedDb.Projects.Add(_project);
        _seedDb.OrgMembers.Add(OrgMember.Create(_org.Id, _admin.Id, MemberRole.Owner));
        _seedDb.ProjectMembers.Add(ProjectMember.Create(_project.Id, _admin.Id, MemberRole.Owner));
        _seedDb.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _seedDb.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ApiTokenCreateAndRevoke_AreAuditedWithTheCredentialMutation()
    {
        var actor = AdminActor();
        var config = new ConfigurationBuilder().Build();
        var create = new CreateApiTokenHandler(_factory, config, _authorization, _auditWriter);
        var created = await create.HandleAsync(new CreateApiTokenCommand(
            new CreateApiTokenRequest(_project.Id, "automation", ApiTokenCapability.TaskRead, null), actor),
            TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        var createdEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.api_token.created" && e.TargetId == created.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, createdEvent.ActorKind);
        Assert.Equal(_admin.Id, createdEvent.ActorUserId);
        Assert.Equal(_org.Id, createdEvent.OrgId);
        Assert.Equal(_project.Id, createdEvent.ProjectId);
        Assert.DoesNotContain(created.Value.Token!, createdEvent.DetailsJson ?? string.Empty);

        var revoke = new RevokeApiTokenHandler(_factory, _authorization, _auditWriter);
        var revoked = await revoke.HandleAsync(new RevokeApiTokenCommand(
            new RevokeApiTokenRequest(_project.Id, created.Value.Id), actor),
            TestContext.Current.CancellationToken);

        Assert.True(revoked.Success, revoked.Error);
        var revokedEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.api_token.revoked" && e.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, revokedEvent.OrgId);
        Assert.Equal(_project.Id, revokedEvent.ProjectId);
        Assert.Equal(2, await _seedDb.AuditEvents.CountAsync(
            e => e.TargetType == "ApiToken" && e.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectMembershipAddAndRemove_AreAuditedWithTheMembershipMutation()
    {
        var actor = AdminActor();
        var add = new AddProjectMemberHandler(_factory, _authorization, _auditWriter);
        var added = await add.HandleAsync(new AddProjectMemberCommand(
            new AddProjectMemberRequest(_project.Id, _invitee.Id, MemberRole.Member), actor),
            TestContext.Current.CancellationToken);

        Assert.True(added.Success, added.Error);
        var addEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.member.added" && e.TargetId == added.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, addEvent.OrgId);
        Assert.Equal(_project.Id, addEvent.ProjectId);
        Assert.Contains("Member", addEvent.DetailsJson);

        var remove = new RemoveProjectMemberHandler(_factory, _authorization, _auditWriter);
        var removed = await remove.HandleAsync(new RemoveProjectMemberCommand(
            new RemoveProjectMemberRequest(_project.Id, _invitee.Id), actor),
            TestContext.Current.CancellationToken);

        Assert.True(removed.Success, removed.Error);
        var removeEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.member.removed" && e.TargetId == added.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, removeEvent.OrgId);
        Assert.Equal(_project.Id, removeEvent.ProjectId);
    }

    [Fact]
    public async Task ProjectInviteCreateRevokeAndAccept_AreAuditedWithoutStoringInviteSecrets()
    {
        var actor = AdminActor();
        var email = new NoopEmailSender();
        var config = new ConfigurationBuilder().Build();

        var created = await new CreateProjectInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter)
            .HandleAsync(new CreateProjectInviteRequest(
                _project.Id, "revoke-me@example.test", MemberRole.Member, actor),
                TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        var createdEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.invite.created" && e.TargetId == created.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, createdEvent.OrgId);
        Assert.Equal(_project.Id, createdEvent.ProjectId);
        Assert.DoesNotContain(created.Value.Token, createdEvent.DetailsJson ?? string.Empty);

        var revoked = await new RevokeProjectInviteHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new RevokeInviteRequest(created.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "project.invite.revoked" && e.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken));

        var acceptInvite = await new CreateProjectInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter)
            .HandleAsync(new CreateProjectInviteRequest(
                _project.Id, _invitee.Email, MemberRole.Member, actor),
                TestContext.Current.CancellationToken);
        Assert.True(acceptInvite.Success, acceptInvite.Error);

        var accepted = await new AcceptInviteHandler(_factory, auditWriter: _auditWriter)
            .HandleAsync(new AcceptInviteRequest(acceptInvite.Value!.Token, Human(_invitee.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "project.invite.accepted" && e.TargetId == acceptInvite.Value.Id.ToString(),
            TestContext.Current.CancellationToken));
    }

    private ActorContext AdminActor() =>
        new(ActorType.Human, _admin.Id, true, null, null, _project.Id, _org.Id, ApiTokenCapability.None);

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    private sealed class NoopEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
