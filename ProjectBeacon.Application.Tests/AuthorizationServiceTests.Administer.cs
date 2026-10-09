namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed partial class AuthorizationServiceTests
{
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
}
