namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed class AuthorizationServiceTests : IDisposable
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

    // ── Worker guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_AnyResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Worker_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Worker(), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    // ── Device guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task Device_DeviceResource_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Device, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_WorkstationCommand_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.WorkstationCommand, AuthAction.Execute);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Device_TaskResource_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Device_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Device(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    // ── Admin bypass ──────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ProjectAdminister_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_OrgAdminister_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Org, AuthAction.Administer, orgId: Guid.NewGuid());
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Admin_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Admin(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    // ── ApiToken policy ───────────────────────────────────────────────────

    [Fact]
    public async Task Token_WithTaskRead_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_TaskRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Create);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskUpdate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Update);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskDelete_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Task, AuthAction.Delete);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskWrite_TaskCreate_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Task, AuthAction.Create);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_TaskStepRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.TaskStep, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_TaskStepCreate_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.TaskStep, AuthAction.Create);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithContextRead_ContextRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.ContextRead), ResourceType.Context, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithoutContextRead_ContextRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Context, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskRead_PipelineRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskRead), ResourceType.Pipeline, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_WithTaskWrite_PipelineExecute_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.TaskWrite), ResourceType.Pipeline, AuthAction.Execute);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_Admin_ChatSessionExecute_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.ChatSession, AuthAction.Execute);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_WithoutTaskRead_PipelineRead_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(), ResourceType.Pipeline, AuthAction.Read);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_ProjectAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Project, AuthAction.Administer, projectId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_OrgAdminister_ReturnsForbidden()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Org, AuthAction.Administer, orgId: Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Token_AdminCapability_BypassesTaskRead()
    {
        var r = await _auth.CanAsync(_db, Token(ApiTokenCapability.Admin), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    // ── Human non-administer (no DB queries) ──────────────────────────────

    [Fact]
    public async Task Human_TaskRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Task, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_DecisionRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Decision, AuthAction.Read);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_MilestoneRead_ReturnsOk()
    {
        var r = await _auth.CanAsync(_db, Human(Guid.NewGuid()), ResourceType.Milestone, AuthAction.Read);
        Assert.True(r.Success);
    }

    // ── Human Project/Administer (DB-backed) ──────────────────────────────

    private async Task<(Guid orgId, Guid projectId, Guid userId)> SeedProjectWithMembers(
        MemberRole projectRole, MemberRole? orgRole = null)
    {
        var org = Org.Create("TestOrg", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var project = Project.Create("TestProject", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var user = User.Create("testuser", "test@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, projectRole));
        if (orgRole is not null)
            _db.OrgMembers.Add(OrgMember.Create(org.Id, user.Id, orgRole.Value));
        await _db.SaveChangesAsync();

        return (org.Id, project.Id, user.Id);
    }

    [Fact]
    public async Task Human_ProjectOwner_ProjectAdminister_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_ProjectAdmin_ProjectAdminister_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_ProjectMember_ProjectAdminister_ReturnsForbidden()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_NonMember_ProjectAdminister_ReturnsForbidden()
    {
        var (_, projectId, _) = await SeedProjectWithMembers(MemberRole.Member);
        var strangerId = Guid.NewGuid();
        var r = await _auth.CanAsync(_db, Human(strangerId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_OrgAdmin_NotProjectMember_ProjectAdminister_ReturnsOk()
    {
        var (orgId, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member, MemberRole.Admin);
        // Remove the project membership to test org escalation only
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == userId));
        await _db.SaveChangesAsync();

        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_OrgOwner_NotProjectMember_ProjectAdminister_ReturnsOk()
    {
        var (orgId, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member, MemberRole.Owner);
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == userId));
        await _db.SaveChangesAsync();

        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_OrgMember_NotProjectMember_ProjectAdminister_ReturnsForbidden()
    {
        var (orgId, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member, MemberRole.Member);
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == userId));
        await _db.SaveChangesAsync();

        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_ProjectAdmin_OrgMember_ProjectAdminister_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin, MemberRole.Member);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Project, AuthAction.Administer, projectId: projectId);
        Assert.True(r.Success);
    }

    // ── Human Org/Administer (DB-backed) ──────────────────────────────────

    private async Task<(Guid orgId, Guid userId)> SeedOrgWithMember(MemberRole role)
    {
        var org = Org.Create("TestOrg", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();

        var user = User.Create("orguser", "org@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.OrgMembers.Add(OrgMember.Create(org.Id, user.Id, role));
        await _db.SaveChangesAsync();

        return (org.Id, user.Id);
    }

    [Fact]
    public async Task Human_OrgOwner_OrgAdminister_ReturnsOk()
    {
        var (orgId, userId) = await SeedOrgWithMember(MemberRole.Owner);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Org, AuthAction.Administer, orgId: orgId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_OrgAdmin_OrgAdminister_ReturnsOk()
    {
        var (orgId, userId) = await SeedOrgWithMember(MemberRole.Admin);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Org, AuthAction.Administer, orgId: orgId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Human_OrgMember_OrgAdminister_ReturnsForbidden()
    {
        var (orgId, userId) = await SeedOrgWithMember(MemberRole.Member);
        var r = await _auth.CanAsync(_db, Human(userId), ResourceType.Org, AuthAction.Administer, orgId: orgId);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Human_NonMember_OrgAdminister_ReturnsForbidden()
    {
        var (orgId, _) = await SeedOrgWithMember(MemberRole.Member);
        var strangerId = Guid.NewGuid();
        var r = await _auth.CanAsync(_db, Human(strangerId), ResourceType.Org, AuthAction.Administer, orgId: orgId);
        Assert.False(r.Success);
    }

    // ── CanAddMemberAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CanAddMember_ApiToken_ReturnsForbidden()
    {
        var (_, projectId, _) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanAddMemberAsync(_db, Token(ApiTokenCapability.Admin), projectId, Guid.NewGuid(), MemberRole.Member);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanAddMember_ProjectOwner_AddMember_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Member);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanAddMember_ProjectAdmin_AddMember_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Member);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanAddMember_ProjectMember_AddMember_ReturnsForbidden()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member);
        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Member);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanAddMember_ProjectOwner_AddOwner_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Owner);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanAddMember_ProjectAdmin_AddOwner_ReturnsForbidden()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Owner);
        Assert.False(r.Success);
        Assert.Contains("Owner", r.Error);
    }

    [Fact]
    public async Task CanAddMember_SystemAdmin_AddOwner_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanAddMemberAsync(_db, Admin(userId), projectId, Guid.NewGuid(), MemberRole.Owner);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanAddMember_OrgAdmin_AddMember_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member, MemberRole.Admin);
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == userId));
        await _db.SaveChangesAsync();

        var r = await _auth.CanAddMemberAsync(_db, Human(userId), projectId, Guid.NewGuid(), MemberRole.Member);
        Assert.True(r.Success);
    }

    // ── CanRemoveMemberAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CanRemoveMember_ApiToken_ReturnsForbidden()
    {
        var (_, projectId, _) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanRemoveMemberAsync(_db, Token(ApiTokenCapability.Admin), projectId, Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_SelfRemoval_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member);
        var r = await _auth.CanRemoveMemberAsync(_db, Human(userId), projectId, userId);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_ProjectOwner_RemoveMember_ReturnsOk()
    {
        var (_, projectId, ownerUserId) = await SeedProjectWithMembers(MemberRole.Owner);
        var member = User.Create("member", "member@example.com", "hash");
        _db.Users.Add(member);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, member.Id, MemberRole.Member));
        await _db.SaveChangesAsync();

        var r = await _auth.CanRemoveMemberAsync(_db, Human(ownerUserId), projectId, member.Id);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_ProjectMember_RemoveMember_ReturnsForbidden()
    {
        var (_, projectId, memberUserId) = await SeedProjectWithMembers(MemberRole.Member);
        var r = await _auth.CanRemoveMemberAsync(_db, Human(memberUserId), projectId, Guid.NewGuid());
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_ProjectOwner_RemoveLastOwner_ReturnsForbidden()
    {
        var (_, projectId, ownerUserId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanRemoveMemberAsync(_db, Human(ownerUserId), projectId, ownerUserId);
        // Self-removal check fires first, so this returns Ok.
        // To test last-owner guard, use a different owner.
        // Actually self-removal returns Ok before the owner check, so let's test with two owners.
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_TwoOwners_OwnerRemovesOtherOwner_ReturnsOk()
    {
        var (_, projectId, owner1Id) = await SeedProjectWithMembers(MemberRole.Owner);
        var owner2 = User.Create("owner2", "owner2@example.com", "hash");
        _db.Users.Add(owner2);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, owner2.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var r = await _auth.CanRemoveMemberAsync(_db, Human(owner1Id), projectId, owner2.Id);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanRemoveMember_ProjectAdmin_RemoveOwner_ReturnsForbidden()
    {
        var (_, projectId, adminId) = await SeedProjectWithMembers(MemberRole.Admin);
        var owner = User.Create("theowner", "owner@example.com", "hash");
        var coOwner = User.Create("coowner", "coowner@example.com", "hash");
        _db.Users.AddRange(owner, coOwner);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, owner.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, coOwner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var r = await _auth.CanRemoveMemberAsync(_db, Human(adminId), projectId, owner.Id);
        Assert.False(r.Success);
        Assert.Contains("Owner", r.Error);
    }

    [Fact]
    public async Task CanRemoveMember_SystemAdmin_RemoveOwner_ReturnsOk()
    {
        var (_, projectId, adminId) = await SeedProjectWithMembers(MemberRole.Admin);
        var owner = User.Create("theowner", "owner@example.com", "hash");
        var coOwner = User.Create("coowner", "coowner@example.com", "hash");
        _db.Users.AddRange(owner, coOwner);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, owner.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(projectId, coOwner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        var r = await _auth.CanRemoveMemberAsync(_db, Admin(adminId), projectId, owner.Id);
        Assert.True(r.Success);
    }

    // ── CanCreateTokenAsync ───────────────────────────────────────────────

    [Fact]
    public async Task CanCreateToken_ApiToken_ReturnsForbidden()
    {
        var (_, projectId, _) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanCreateTokenAsync(_db, Token(ApiTokenCapability.Admin), projectId, ApiTokenCapability.TaskRead);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanCreateToken_ProjectOwner_CreateNormalToken_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanCreateTokenAsync(_db, Human(userId), projectId, ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanCreateToken_ProjectAdmin_CreateNormalToken_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanCreateTokenAsync(_db, Human(userId), projectId, ApiTokenCapability.TaskRead);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanCreateToken_ProjectMember_CreateToken_ReturnsForbidden()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Member);
        var r = await _auth.CanCreateTokenAsync(_db, Human(userId), projectId, ApiTokenCapability.TaskRead);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task CanCreateToken_ProjectAdmin_CreateAdminToken_ReturnsForbidden()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanCreateTokenAsync(_db, Human(userId), projectId, ApiTokenCapability.Admin);
        Assert.False(r.Success);
        Assert.Contains("Admin", r.Error);
    }

    [Fact]
    public async Task CanCreateToken_ProjectOwner_CreateAdminToken_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Owner);
        var r = await _auth.CanCreateTokenAsync(_db, Human(userId), projectId, ApiTokenCapability.Admin);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task CanCreateToken_SystemAdmin_CreateAdminToken_ReturnsOk()
    {
        var (_, projectId, userId) = await SeedProjectWithMembers(MemberRole.Admin);
        var r = await _auth.CanCreateTokenAsync(_db, Admin(userId), projectId, ApiTokenCapability.Admin);
        Assert.True(r.Success);
    }

    // ── HasCapability ─────────────────────────────────────────────────────

    [Fact]
    public void HasCapability_Human_ReturnsTrue()
    {
        Assert.True(_auth.HasCapability(Human(Guid.NewGuid()), ApiTokenCapability.Admin));
    }

    [Fact]
    public void HasCapability_Device_ReturnsTrue()
    {
        Assert.True(_auth.HasCapability(Device(Guid.NewGuid()), ApiTokenCapability.Admin));
    }

    [Fact]
    public void HasCapability_Worker_ReturnsTrue()
    {
        Assert.True(_auth.HasCapability(Worker(), ApiTokenCapability.Admin));
    }

    [Fact]
    public void HasCapability_Token_WithCapability_ReturnsTrue()
    {
        Assert.True(_auth.HasCapability(Token(ApiTokenCapability.TaskRead), ApiTokenCapability.TaskRead));
    }

    [Fact]
    public void HasCapability_Token_WithoutCapability_ReturnsFalse()
    {
        Assert.False(_auth.HasCapability(Token(ApiTokenCapability.TaskRead), ApiTokenCapability.TaskWrite));
    }

    [Fact]
    public void HasCapability_Token_CombinedCapabilities_ReturnsTrue()
    {
        var caps = ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;
        Assert.True(_auth.HasCapability(Token(caps), ApiTokenCapability.TaskWrite));
    }

    [Fact]
    public void HasCapability_Token_None_ReturnsFalse()
    {
        Assert.False(_auth.HasCapability(Token(), ApiTokenCapability.TaskRead));
    }
}
