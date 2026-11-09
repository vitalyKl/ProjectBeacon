namespace ProjectBeacon.Application.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Devices;
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
using ProjectBeacon.Infrastructure.Security;
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
    private readonly Org _otherOrg;
    private readonly Project _project;

    public AuditInstrumentationTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);

        _admin = User.Create("audit-admin", "audit-admin@example.test", "hash", isAdmin: true);
        _invitee = User.Create("audit-invitee", "audit-invitee@example.test", "hash");
        _org = Org.Create("Audit Org");
        _otherOrg = Org.Create("Other Audit Org");
        _project = Project.Create("Audit Project", null, _org.Id);

        _seedDb.Users.AddRange(_admin, _invitee);
        _seedDb.Orgs.AddRange(_org, _otherOrg);
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

        var unrelatedTenant = new TenantContext();
        unrelatedTenant.Assign(null, _otherOrg.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, unrelatedTenant);
        var accepted = await new AcceptInviteHandler(scopedFactory, auditWriter: _auditWriter)
            .HandleAsync(new AcceptInviteRequest(acceptInvite.Value!.Token, Human(_invitee.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(accepted.Success, accepted.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "project.invite.accepted" && e.TargetId == acceptInvite.Value.Id.ToString(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrgInviteCreateRevokeAndAccept_AreAuditedWithTheCorrectTenantScope()
    {
        var actor = AdminActor();
        var email = new NoopEmailSender();
        var config = new ConfigurationBuilder().Build();
        var create = new CreateOrgInviteHandler(_factory, email, _authorization, config, auditWriter: _auditWriter);

        var toRevoke = await create.HandleAsync(
            new CreateOrgInviteRequest(_org.Id, "revoke-org@example.test", MemberRole.Member, actor),
            TestContext.Current.CancellationToken);
        Assert.True(toRevoke.Success, toRevoke.Error);

        var createEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.invite.created" && e.TargetId == toRevoke.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, createEvent.OrgId);
        Assert.Null(createEvent.ProjectId);
        Assert.DoesNotContain(toRevoke.Value.Email, createEvent.DetailsJson ?? string.Empty);
        Assert.DoesNotContain(toRevoke.Value.Token, createEvent.DetailsJson ?? string.Empty);

        var revoked = await new RevokeOrgInviteHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new RevokeInviteRequest(toRevoke.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "org.invite.revoked" && e.TargetId == toRevoke.Value.Id.ToString(),
            TestContext.Current.CancellationToken));

        var toAccept = await create.HandleAsync(
            new CreateOrgInviteRequest(_org.Id, _invitee.Email, MemberRole.Member, actor),
            TestContext.Current.CancellationToken);
        Assert.True(toAccept.Success, toAccept.Error);

        // Simulate the invitee arriving from an unrelated organization context. Invite
        // lookup must use its opaque token, while the mutation/audit write must be bound
        // to the invitation's organization instead of the caller's stale tenant.
        var otherTenant = new TenantContext();
        otherTenant.Assign(null, _otherOrg.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, otherTenant);
        var accepted = await new AcceptInviteHandler(scopedFactory, auditWriter: _auditWriter)
            .HandleAsync(new AcceptInviteRequest(toAccept.Value!.Token, Human(_invitee.Id)),
                TestContext.Current.CancellationToken);

        Assert.True(accepted.Success, accepted.Error);
        Assert.True(await _seedDb.OrgMembers.AnyAsync(
            m => m.OrgId == _org.Id && m.UserId == _invitee.Id,
            TestContext.Current.CancellationToken));

        var acceptedEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.invite.accepted" && e.TargetId == toAccept.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, acceptedEvent.OrgId);
        Assert.Null(acceptedEvent.ProjectId);
    }

    [Fact]
    public async Task RegistrationByOrgAndProjectInvite_AuditsAccountAndAcceptanceAtomically()
    {
        var config = new ConfigurationBuilder().Build();
        var email = new NoopEmailSender();
        var registration = new RegisterHandler(
            _factory, new BcryptPasswordHasher(), config, auditWriter: _auditWriter);

        var orgInvite = await new CreateOrgInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter)
            .HandleAsync(new CreateOrgInviteRequest(
                _org.Id, "new-org-owner@example.test", MemberRole.Owner, AdminActor()),
                TestContext.Current.CancellationToken);
        Assert.True(orgInvite.Success, orgInvite.Error);

        var orgRegistration = await registration.HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "new-org-owner", "new-org-owner@example.test", "password123", orgInvite.Value!.Token)),
            TestContext.Current.CancellationToken);
        Assert.True(orgRegistration.Success, orgRegistration.Error);

        var orgAccepted = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.invite.accepted" && e.TargetId == orgInvite.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        var orgAccount = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "account.registered" && e.TargetId == orgRegistration.Value!.UserId.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, orgAccepted.ActorKind);
        Assert.Equal(orgRegistration.Value.UserId, orgAccepted.ActorUserId);
        Assert.Equal(_org.Id, orgAccepted.OrgId);
        Assert.Null(orgAccepted.ProjectId);
        Assert.Equal(_org.Id, orgAccount.OrgId);
        Assert.Null(orgAccount.ProjectId);
        Assert.DoesNotContain(orgInvite.Value.Token, orgAccepted.DetailsJson ?? string.Empty);
        Assert.DoesNotContain("new-org-owner@example.test", orgAccount.DetailsJson ?? string.Empty);
        Assert.True(await _seedDb.OrgMembers.AnyAsync(
            m => m.OrgId == _org.Id && m.UserId == orgRegistration.Value.UserId,
            TestContext.Current.CancellationToken));

        var projectInvite = await new CreateProjectInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter)
            .HandleAsync(new CreateProjectInviteRequest(
                _project.Id, "new-project-member@example.test", MemberRole.Member, AdminActor()),
                TestContext.Current.CancellationToken);
        Assert.True(projectInvite.Success, projectInvite.Error);

        var projectRegistration = await registration.HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "new-project-member", "new-project-member@example.test", "password123", projectInvite.Value!.Token)),
            TestContext.Current.CancellationToken);
        Assert.True(projectRegistration.Success, projectRegistration.Error);

        var projectAccepted = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.invite.accepted" && e.TargetId == projectInvite.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        var projectAccount = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "account.registered" && e.TargetId == projectRegistration.Value!.UserId.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, projectAccepted.ActorKind);
        Assert.Equal(projectRegistration.Value.UserId, projectAccepted.ActorUserId);
        Assert.Equal(_org.Id, projectAccepted.OrgId);
        Assert.Equal(_project.Id, projectAccepted.ProjectId);
        Assert.Equal(_org.Id, projectAccount.OrgId);
        Assert.Equal(_project.Id, projectAccount.ProjectId);
        Assert.DoesNotContain(projectInvite.Value.Token, projectAccepted.DetailsJson ?? string.Empty);
        Assert.DoesNotContain("new-project-member@example.test", projectAccount.DetailsJson ?? string.Empty);
        Assert.True(await _seedDb.ProjectMembers.AnyAsync(
            m => m.ProjectId == _project.Id && m.UserId == projectRegistration.Value.UserId,
            TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task OrganizationAndProjectLifecycle_AreAuditedWithCorrectTenantScope()
    {
        var priorTenant = new TenantContext();
        priorTenant.Assign(_project.Id, _org.Id, unscoped: false);

        var createdOrg = await new CreateOrgHandler(_factory, priorTenant, _auditWriter)
            .HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Created by audit test", null), AdminActor()),
                TestContext.Current.CancellationToken);
        Assert.True(createdOrg.Success, createdOrg.Error);

        var orgCreatedEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.created" && e.TargetId == createdOrg.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(createdOrg.Value.Id, orgCreatedEvent.OrgId);
        Assert.Null(orgCreatedEvent.ProjectId);

        var renamedOrg = await new UpdateOrgHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new UpdateOrgCommand(
                new UpdateOrgRequest(createdOrg.Value.Id, "Renamed org", null), AdminActor()),
                TestContext.Current.CancellationToken);
        Assert.True(renamedOrg.Success, renamedOrg.Error);

        var orgUpdatedEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.updated" && e.TargetId == createdOrg.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(createdOrg.Value.Id, orgUpdatedEvent.OrgId);
        Assert.Null(orgUpdatedEvent.ProjectId);

        var denied = await new UpdateOrgHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new UpdateOrgCommand(
                new UpdateOrgRequest(createdOrg.Value.Id, "Unauthorized rename", null),
                Human(_invitee.Id)),
                TestContext.Current.CancellationToken);
        Assert.False(denied.Success);

        var orgTenant = new TenantContext();
        orgTenant.Assign(null, _org.Id, unscoped: false);
        var createdProject = await new CreateProjectHandler(_factory, _authorization, orgTenant, _auditWriter)
            .HandleAsync(new CreateProjectCommand(
                new CreateProjectRequest("Audited project", null, _org.Id), AdminActor()),
                TestContext.Current.CancellationToken);
        Assert.True(createdProject.Success, createdProject.Error);

        var projectCreatedEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.created" && e.TargetId == createdProject.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, projectCreatedEvent.OrgId);
        Assert.Equal(createdProject.Value.Id, projectCreatedEvent.ProjectId);

        var scopedActor = new ActorContext(
            ActorType.Human, _admin.Id, true, null, null,
            createdProject.Value.Id, _org.Id, ApiTokenCapability.None);
        var updatedProject = await new UpdateProjectHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new UpdateProjectCommand(
                new UpdateProjectRequest(createdProject.Value.Id, "Updated audited project", null), scopedActor),
                TestContext.Current.CancellationToken);
        Assert.True(updatedProject.Success, updatedProject.Error);

        var deletedProject = await new DeleteProjectHandler(_factory, _authorization, _auditWriter)
            .HandleAsync(new DeleteProjectCommand(createdProject.Value.Id, scopedActor),
                TestContext.Current.CancellationToken);
        Assert.True(deletedProject.Success, deletedProject.Error);

        var lifecycle = await _seedDb.AuditEvents
            .Where(e => e.TargetType == "Project" && e.TargetId == createdProject.Value.Id.ToString())
            .OrderBy(e => e.Action)
            .Select(e => e.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("project.created", lifecycle);
        Assert.Contains("project.updated", lifecycle);
        Assert.Contains("project.deleted", lifecycle);
    }

    [Fact]
    public async Task DeviceEnrollmentRotationAndRevocation_AreAuditedWithoutDeviceSecrets()
    {
        var actor = AdminActor();
        var create = new CreateDeviceHandler(_factory, auditWriter: _auditWriter);

        var first = await create.HandleAsync(new CreateDeviceCommand(
            new CreateDeviceRequest("Audit device", "audit-fingerprint", _admin.Id, Actor: actor)),
            TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        Assert.False(string.IsNullOrWhiteSpace(first.Value!.Token));

        var enrolled = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "device.enrolled" && e.TargetId == first.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first.Value.Token!, enrolled.DetailsJson ?? string.Empty);
        Assert.DoesNotContain("audit-fingerprint", enrolled.DetailsJson ?? string.Empty);

        var rotated = await create.HandleAsync(new CreateDeviceCommand(
            new CreateDeviceRequest("Renamed audit device", "audit-fingerprint", _admin.Id, Actor: actor)),
            TestContext.Current.CancellationToken);
        Assert.True(rotated.Success, rotated.Error);
        Assert.NotEqual(first.Value.Token, rotated.Value!.Token);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "device.token.rotated" && e.TargetId == first.Value.Id.ToString(),
            TestContext.Current.CancellationToken));

        var revoked = await new RevokeDeviceHandler(_factory, _auditWriter)
            .HandleAsync(new RevokeDeviceCommand(
                new RevokeDeviceRequest(first.Value.Id, _admin.Id, actor)),
                TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "device.revoked" && e.TargetId == first.Value.Id.ToString(),
            TestContext.Current.CancellationToken));
    }

    private ActorContext AdminActor() =>
        new(ActorType.Human, _admin.Id, true, null, null, _project.Id, _org.Id, ApiTokenCapability.None);

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    private sealed class TenantBoundDbFactory(IBeaconDbFactory inner, ITenantContext tenant) : IBeaconDbFactory
    {
        public IBeaconDb CreateDbContext() => inner.CreateDbContext(tenant);

        public IBeaconDb CreateDbContext(ITenantContext tenantContext) => inner.CreateDbContext(tenantContext);
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
