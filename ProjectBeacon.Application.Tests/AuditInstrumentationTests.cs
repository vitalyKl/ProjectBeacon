namespace ProjectBeacon.Application.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Projects;
using ProjectBeacon.Application.Security;
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
            _factory, email, _authorization, config, auditWriter: _auditWriter, adminGate: new DatabaseAdminGate(_factory))
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

        var revoked = await new RevokeProjectInviteHandler(_factory, _authorization, _auditWriter, new DatabaseAdminGate(_factory))
            .HandleAsync(new RevokeInviteRequest(created.Value.Id, actor), TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);
        Assert.True(await _seedDb.AuditEvents.AnyAsync(
            e => e.Action == "project.invite.revoked" && e.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken));

        var acceptInvite = await new CreateProjectInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter, adminGate: new DatabaseAdminGate(_factory))
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
        var create = new CreateOrgInviteHandler(_factory, email, _authorization, config, auditWriter: _auditWriter, adminGate: new DatabaseAdminGate(_factory));

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

        var revoked = await new RevokeOrgInviteHandler(_factory, _authorization, _auditWriter, new DatabaseAdminGate(_factory))
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
    public async Task OperatorOrgInviteLifecycle_UsesExplicitTargetTenantAndVerifiedPermission()
    {
        var ambient = new TenantContext();
        ambient.Assign(null, _org.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, ambient);
        var adminGate = new DatabaseAdminGate(scopedFactory);
        var email = new NoopEmailSender();
        var config = new ConfigurationBuilder().Build();
        var actor = AdminActor();

        Assert.False(await _seedDb.OrgMembers.AnyAsync(
            member => member.OrgId == _otherOrg.Id && member.UserId == _admin.Id,
            TestContext.Current.CancellationToken));

        var create = await new CreateOrgInviteHandler(
            scopedFactory, email, _authorization, config, auditWriter: _auditWriter, adminGate: adminGate)
            .HandleAsync(new CreateOrgInviteRequest(
                _otherOrg.Id, "external-owner@example.test", MemberRole.Owner, actor),
                TestContext.Current.CancellationToken);
        Assert.True(create.Success, create.Error);

        var list = await new ListOrgInvitesHandler(scopedFactory, _authorization, adminGate)
            .HandleAsync(new ListInvitesRequest(_otherOrg.Id, actor), TestContext.Current.CancellationToken);
        Assert.True(list.Success, list.Error);
        Assert.Single(list.Value!);
        Assert.Equal(create.Value!.Id, list.Value![0].Id);

        var revoked = await new RevokeOrgInviteHandler(
            scopedFactory, _authorization, _auditWriter, adminGate)
            .HandleAsync(new RevokeInviteRequest(create.Value.Id, actor, _otherOrg.Id),
                TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);

        var eventRow = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "org.invite.created" && e.TargetId == create.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_otherOrg.Id, eventRow.OrgId);
        Assert.Null(eventRow.ProjectId);
        Assert.DoesNotContain(create.Value.Token, eventRow.DetailsJson ?? string.Empty);
    }

    [Fact]
    public async Task RegistrationByOrgAndProjectInvite_AuditsAccountAndAcceptanceAtomically()
    {
        var config = new ConfigurationBuilder().Build();
        var email = new NoopEmailSender();
        var registration = new RegisterHandler(
            _factory, new BcryptPasswordHasher(), config, auditWriter: _auditWriter);

        var orgInvite = await new CreateOrgInviteHandler(
            _factory, email, _authorization, config, auditWriter: _auditWriter, adminGate: new DatabaseAdminGate(_factory))
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
            _factory, email, _authorization, config, auditWriter: _auditWriter, adminGate: new DatabaseAdminGate(_factory))
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
    public async Task OrganizationProvisioningCreateAndUpdate_AreAuditedWithoutOperatorMembership()
    {
        var actor = AdminActor();
        var gate = new DatabaseAdminGate(_factory);
        var create = new CreateOrgHandler(_factory, gate, _auditWriter);

        var created = await create.HandleAsync(
            new CreateOrgCommand(new CreateOrgRequest("Provisioned Alpha", "Operator-created"), actor),
            TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        var createdEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "org.created" && item.TargetId == created.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, createdEvent.ActorKind);
        Assert.Equal(_admin.Id, createdEvent.ActorUserId);
        Assert.Equal(created.Value.Id, createdEvent.OrgId);
        Assert.Null(createdEvent.ProjectId);
        Assert.False(await _seedDb.OrgMembers.IgnoreQueryFilters().AnyAsync(
            member => member.OrgId == created.Value.Id,
            TestContext.Current.CancellationToken));

        var update = new UpdateOrgHandler(_factory, _authorization, gate, _auditWriter);
        var updated = await update.HandleAsync(
            new UpdateOrgCommand(
                new UpdateOrgRequest(created.Value.Id, "Provisioned Alpha Updated", null),
                actor),
            TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        Assert.Equal("Provisioned Alpha Updated", updated.Value!.Name);
        var updatedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "org.updated" && item.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, updatedEvent.ActorKind);
        Assert.Equal(_admin.Id, updatedEvent.ActorUserId);
        Assert.Equal(created.Value.Id, updatedEvent.OrgId);
        Assert.Null(updatedEvent.ProjectId);
        Assert.Equal(2, await _seedDb.AuditEvents.CountAsync(
            item => item.OrgId == created.Value.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await _seedDb.OrgMembers.IgnoreQueryFilters().AnyAsync(
            member => member.OrgId == created.Value.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectCreation_IsAuditedWithTheProjectMutation()
    {
        var actor = AdminActor();
        var handler = new CreateProjectHandler(
            _factory, _authorization, new TenantContext(), new DatabaseAdminGate(_factory), _auditWriter);

        var created = await handler.HandleAsync(
            new CreateProjectCommand(
                new CreateProjectRequest("Audited Project", null, _org.Id),
                actor),
            TestContext.Current.CancellationToken);

        Assert.True(created.Success, created.Error);
        var auditEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.created" && e.TargetId == created.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, auditEvent.ActorKind);
        Assert.Equal(_admin.Id, auditEvent.ActorUserId);
        Assert.Equal(_org.Id, auditEvent.OrgId);
        Assert.Equal(created.Value.Id, auditEvent.ProjectId);
        Assert.Equal("""{"newState":"Active"}""", auditEvent.DetailsJson);
        Assert.False(await _seedDb.ProjectMembers.IgnoreQueryFilters().AnyAsync(
            member => member.ProjectId == created.Value.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectUpdate_IsAuditedWithTheProjectMutation()
    {
        var tenant = new TenantContext();
        tenant.Assign(_project.Id, _org.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, tenant);
        var handler = new UpdateProjectHandler(scopedFactory, _authorization, _auditWriter);

        var updated = await handler.HandleAsync(
            new UpdateProjectCommand(
                new UpdateProjectRequest(_project.Id, "Updated Audit Project", null),
                AdminActor()),
            TestContext.Current.CancellationToken);

        Assert.True(updated.Success, updated.Error);
        var auditEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.updated" && e.TargetId == _project.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, auditEvent.ActorUserId);
        Assert.Equal(_org.Id, auditEvent.OrgId);
        Assert.Equal(_project.Id, auditEvent.ProjectId);
        Assert.Equal("""{"reasonCode":"metadata-updated"}""", auditEvent.DetailsJson);
    }

    [Fact]
    public async Task ProjectDeletion_IsAuditedWithTheCleanupTransaction()
    {
        var tenant = new TenantContext();
        tenant.Assign(_project.Id, _org.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, tenant);
        var handler = new DeleteProjectHandler(scopedFactory, _authorization, _auditWriter);

        var deleted = await handler.HandleAsync(
            new DeleteProjectCommand(_project.Id, AdminActor()),
            TestContext.Current.CancellationToken);

        Assert.True(deleted.Success, deleted.Error);
        Assert.True(deleted.Value);
        var auditEvent = await _seedDb.AuditEvents.SingleAsync(
            e => e.Action == "project.deleted" && e.TargetId == _project.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, auditEvent.ActorUserId);
        Assert.Equal(_org.Id, auditEvent.OrgId);
        Assert.Equal(_project.Id, auditEvent.ProjectId);
        Assert.Equal("""{"previousState":"Active","newState":"Deleted"}""", auditEvent.DetailsJson);
        Assert.False(await _seedDb.Projects.AnyAsync(
            p => p.Id == _project.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeviceCreateRotateAndRevoke_AreAuditedWithoutCredentialMaterial()
    {
        var actor = Human(_admin.Id);
        var create = new CreateDeviceHandler(_factory, auditWriter: _auditWriter);

        var created = await create.HandleAsync(
            new CreateDeviceCommand(
                new CreateDeviceRequest("Audit Workstation", "fingerprint-audit", _admin.Id),
                actor),
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        Assert.NotNull(created.Value!.Token);

        var createdEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "device.created" && item.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, createdEvent.ActorUserId);
        Assert.Null(createdEvent.OrgId);
        Assert.Null(createdEvent.ProjectId);
        Assert.DoesNotContain(created.Value.Token, createdEvent.DetailsJson ?? string.Empty);

        var rotated = await create.HandleAsync(
            new CreateDeviceCommand(
                new CreateDeviceRequest("Audit Workstation Renamed", "fingerprint-audit", _admin.Id),
                actor),
            TestContext.Current.CancellationToken);
        Assert.True(rotated.Success, rotated.Error);
        Assert.Equal(created.Value.Id, rotated.Value!.Id);
        Assert.NotEqual(created.Value.Token, rotated.Value.Token);

        var rotatedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "device.token.rotated" && item.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, rotatedEvent.ActorUserId);
        Assert.DoesNotContain(rotated.Value.Token!, rotatedEvent.DetailsJson ?? string.Empty);

        var stored = await _seedDb.DaemonDevices.SingleAsync(
            device => device.Id == created.Value.Id,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(stored.TokenHash, createdEvent.DetailsJson ?? string.Empty);
        Assert.DoesNotContain(stored.TokenHash, rotatedEvent.DetailsJson ?? string.Empty);

        var revoke = new RevokeDeviceHandler(_factory, _auditWriter);
        var revoked = await revoke.HandleAsync(
            new RevokeDeviceCommand(new RevokeDeviceRequest(created.Value.Id, _admin.Id), actor),
            TestContext.Current.CancellationToken);
        Assert.True(revoked.Success, revoked.Error);

        var revokedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "device.revoked" && item.TargetId == created.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_admin.Id, revokedEvent.ActorUserId);
        Assert.Null(revokedEvent.OrgId);
        Assert.Null(revokedEvent.ProjectId);
        Assert.Equal("""{"newState":"Revoked"}""", revokedEvent.DetailsJson);
    }

    [Fact]
    public async Task OpenRegistration_IsAuditedAsAPlatformWideAccountEvent()
    {
        var registration = new RegisterHandler(
            _factory, new BcryptPasswordHasher(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["AUTH_LOCAL_INVITE_ONLY"] = "false"
            }).Build(),
            auditWriter: _auditWriter);

        var result = await registration.HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "open-registration", "open-registration@example.test", "password123")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        var auditEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.registered"
                && item.TargetId == result.Value!.UserId.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, auditEvent.ActorKind);
        Assert.Equal(result.Value.UserId, auditEvent.ActorUserId);
        Assert.Null(auditEvent.OrgId);
        Assert.Null(auditEvent.ProjectId);
        Assert.Equal("""{"newState":"Active"}""", auditEvent.DetailsJson);
        Assert.DoesNotContain("open-registration@example.test", auditEvent.DetailsJson ?? string.Empty);
    }

    [Fact]
    public async Task PasswordFlows_AuditOnlyAccountIdentifiersAndMethodCodes()
    {
        var passwords = new BcryptPasswordHasher();
        var user = User.Create(
            "password-audit-user",
            "password-audit-user@example.test",
            passwords.Hash("initial-password"));
        _seedDb.Users.Add(user);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var mail = new RecordingEmailSender();
        var forgot = new ForgotPasswordHandler(
            _factory, mail, new ConfigurationBuilder().Build(), _auditWriter);
        var requested = await forgot.HandleAsync(
            new ForgotPasswordRequest(user.Email),
            TestContext.Current.CancellationToken);
        Assert.True(requested.Success);

        var requestedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.password_reset_requested"
                && item.TargetId == user.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.System, requestedEvent.ActorKind);
        Assert.Null(requestedEvent.ActorUserId);
        Assert.DoesNotContain(user.Email, requestedEvent.DetailsJson ?? string.Empty);

        var tokenMarker = "token=";
        var tokenStart = mail.Body.IndexOf(tokenMarker, StringComparison.Ordinal);
        Assert.True(tokenStart >= 0);
        var rawToken = mail.Body[(tokenStart + tokenMarker.Length)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

        var reset = new ResetPasswordHandler(_factory, passwords, auditWriter: _auditWriter);
        var resetResult = await reset.HandleAsync(
            new ResetPasswordRequest(rawToken, "reset-password-1"),
            TestContext.Current.CancellationToken);
        Assert.True(resetResult.Success, resetResult.Error);

        var resetEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.password_reset"
                && item.TargetId == user.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, resetEvent.ActorKind);
        Assert.Equal(user.Id, resetEvent.ActorUserId);
        Assert.Equal("""{"authMethod":"email-reset-token"}""", resetEvent.DetailsJson);

        var change = new ChangePasswordHandler(_factory, passwords, auditWriter: _auditWriter);
        var changeResult = await change.HandleAsync(
            new ChangePasswordRequest(user.Id, "reset-password-1", "changed-password-2"),
            TestContext.Current.CancellationToken);
        Assert.True(changeResult.Success, changeResult.Error);

        var changedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.password_changed"
                && item.TargetId == user.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(user.Id, changedEvent.ActorUserId);
        Assert.Equal("""{"authMethod":"current-password"}""", changedEvent.DetailsJson);
    }

    [Fact]
    public async Task LoginSuccessAndFailures_AreAuditedWithSafeReasonCodes()
    {
        var passwords = new BcryptPasswordHasher();
        var user = User.Create(
            "login-audit-user",
            "login-audit-user@example.test",
            passwords.Hash("correct-password"));
        _seedDb.Users.Add(user);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWT:Secret"] = "test-jwt-secret-that-is-long-enough-for-testing"
            })
            .Build();
        var login = new LoginHandler(
            _factory, passwords, new TestTokenIssuer(), configuration, _auditWriter);

        var failed = await login.HandleAsync(
            new LoginCommand(new LoginRequest("login-audit-user", "wrong-password")),
            TestContext.Current.CancellationToken);
        Assert.False(failed.Success);

        var failedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.login.failed"
                && item.TargetId == user.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.System, failedEvent.ActorKind);
        Assert.Equal(AuditOutcome.Failed, failedEvent.Outcome);
        Assert.Equal("""{"reasonCode":"invalid-credentials"}""", failedEvent.DetailsJson);

        var success = await login.HandleAsync(
            new LoginCommand(new LoginRequest("login-audit-user", "correct-password")),
            TestContext.Current.CancellationToken);
        Assert.True(success.Success, success.Error);
        Assert.Equal("test-token", success.Value!.Token);

        var successEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "account.login.succeeded"
                && item.TargetId == user.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(AuditActorKind.Human, successEvent.ActorKind);
        Assert.Equal(user.Id, successEvent.ActorUserId);
        Assert.Equal(AuditOutcome.Success, successEvent.Outcome);
        Assert.Equal("""{"authMethod":"password"}""", successEvent.DetailsJson);
    }

    [Fact]
    public void AuditEventFactory_RecordsOnlyThePrincipalForMachineActors()
    {
        var tokenId = Guid.NewGuid();
        var tokenActor = new ActorContext(
            ActorType.ApiToken, _admin.Id, false, tokenId, null,
            _project.Id, _org.Id, ApiTokenCapability.TaskRead);
        var tokenEvent = AuditEventFactory.ForActor(
            tokenActor, "test.token.action", "Project", _project.Id.ToString(),
            _org.Id, _project.Id);

        Assert.Equal(AuditActorKind.ApiToken, tokenEvent.ActorKind);
        Assert.Null(tokenEvent.ActorUserId);
        Assert.Equal(tokenId, tokenEvent.ActorTokenId);
        Assert.Null(tokenEvent.ActorDeviceId);

        var deviceId = Guid.NewGuid();
        var deviceActor = new ActorContext(
            ActorType.Device, _admin.Id, false, null, deviceId,
            null, null, ApiTokenCapability.None);
        var deviceEvent = AuditEventFactory.ForActor(
            deviceActor, "test.device.action", "Device", deviceId.ToString(),
            null, null);

        Assert.Equal(AuditActorKind.Device, deviceEvent.ActorKind);
        Assert.Null(deviceEvent.ActorUserId);
        Assert.Null(deviceEvent.ActorTokenId);
        Assert.Equal(deviceId, deviceEvent.ActorDeviceId);
    }

    [Fact]
    public void AuditCsvWriter_EscapesSpreadsheetFormulasAndPreservesUtf8()
    {
        var entry = new AuditEntry(
            Guid.NewGuid(),
            DateTime.UtcNow,
            AuditActorKind.Human,
            _admin.Id,
            null,
            null,
            "account.login.succeeded",
            "User",
            _admin.Id.ToString(),
            null,
            null,
            AuditOutcome.Success,
            null,
            "=SUM(1,1)",
            null,
            """{"authMethod":"password"}""");

        var csv = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetString(AuditCsvWriter.Encode([entry]));

        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"'=SUM(1,1)\"", csv);
        Assert.Contains("account.login.succeeded", csv);
    }

    [Fact]
    public async Task ProjectRuntimeAttachUpdateAndDetach_AreAuditedWithoutLocalRoot()
    {
        var actor = Human(_admin.Id);
        var deviceHandler = new CreateDeviceHandler(_factory, auditWriter: _auditWriter);
        var device = await deviceHandler.HandleAsync(
            new CreateDeviceCommand(
                new CreateDeviceRequest("Runtime Audit Device", "runtime-audit-fingerprint", _admin.Id),
                actor),
            TestContext.Current.CancellationToken);
        Assert.True(device.Success, device.Error);

        var tenant = new TenantContext();
        tenant.Assign(_project.Id, _org.Id, unscoped: false);
        var scopedFactory = new TenantBoundDbFactory(_factory, tenant);
        var adminGate = new DatabaseAdminGate(_factory);
        var attach = new AttachRuntimeHandler(scopedFactory, adminGate, _auditWriter);

        var attached = await attach.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(
                _project.Id, device.Value!.Id, actor, "/private/local/root")),
            TestContext.Current.CancellationToken);
        Assert.True(attached.Success, attached.Error);

        var attachedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "project.runtime.attached"
                && item.TargetId == attached.Value!.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, attachedEvent.OrgId);
        Assert.Equal(_project.Id, attachedEvent.ProjectId);
        Assert.Equal("""{"newState":"Attached"}""", attachedEvent.DetailsJson);
        Assert.DoesNotContain("/private/local/root", attachedEvent.DetailsJson ?? string.Empty);

        var updated = await attach.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(
                _project.Id, device.Value.Id, actor, "/another/private/root")),
            TestContext.Current.CancellationToken);
        Assert.True(updated.Success, updated.Error);

        var updatedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "project.runtime.updated"
                && item.TargetId == attached.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal("""{"reasonCode":"local-root-updated"}""", updatedEvent.DetailsJson);
        Assert.DoesNotContain("/another/private/root", updatedEvent.DetailsJson ?? string.Empty);

        var detach = new DetachRuntimeHandler(scopedFactory, adminGate, _auditWriter);
        var detached = await detach.HandleAsync(
            new DetachRuntimeCommand(new DetachRuntimeRequest(attached.Value.Id, actor)),
            TestContext.Current.CancellationToken);
        Assert.True(detached.Success, detached.Error);

        var detachedEvent = await _seedDb.AuditEvents.SingleAsync(
            item => item.Action == "project.runtime.detached"
                && item.TargetId == attached.Value.Id.ToString(),
            TestContext.Current.CancellationToken);
        Assert.Equal(_org.Id, detachedEvent.OrgId);
        Assert.Equal(_project.Id, detachedEvent.ProjectId);
        Assert.Equal("""{"newState":"Detached"}""", detachedEvent.DetailsJson);
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

    private sealed class RecordingEmailSender : IEmailSender
    {
        public bool IsConfigured => true;
        public string Body { get; private set; } = string.Empty;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
        {
            Body = textBody;
            return Task.CompletedTask;
        }
    }

    private sealed class TestTokenIssuer : ITokenIssuer
    {
        public string GenerateToken(
            User user,
            string secretKey,
            TimeSpan expiration = default,
            Guid? projectId = null,
            Guid? orgId = null) => "test-token";
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
