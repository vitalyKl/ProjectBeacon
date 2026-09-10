namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class OrgTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var org = Org.Create("Test Org", "Description");

        Assert.NotEqual(Guid.Empty, org.Id);
        Assert.Equal("Test Org", org.Name);
        Assert.Equal("Description", org.Description);
        Assert.Null(org.UpdatedAt);
    }

    [Fact]
    public void Update_UpdatesName()
    {
        var org = Org.Create("Old Name", null);
        org.Update("New Name");

        Assert.Equal("New Name", org.Name);
        Assert.NotNull(org.UpdatedAt);
    }

    [Fact]
    public void Update_UpdatesDescription()
    {
        var org = Org.Create("Name", "Old Desc");
        org.Update(description: "New Desc");

        Assert.Equal("New Desc", org.Description);
    }

    [Fact]
    public void Update_PartiallyUpdates()
    {
        var org = Org.Create("Name", "Desc");
        org.Update(name: "New Name");

        Assert.Equal("New Name", org.Name);
        Assert.Equal("Desc", org.Description);
    }
}

public sealed class OrgMemberTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var member = OrgMember.Create(orgId, userId, MemberRole.Admin);

        Assert.NotEqual(Guid.Empty, member.Id);
        Assert.Equal(orgId, member.OrgId);
        Assert.Equal(userId, member.UserId);
        Assert.Equal(MemberRole.Admin, member.Role);
    }
}

public sealed class OrgInviteTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = OrgInvite.Create(orgId, "test@example.com", MemberRole.Member, userId);

        Assert.NotEqual(Guid.Empty, invite.Id);
        Assert.Equal(orgId, invite.OrgId);
        Assert.Equal("test@example.com", invite.Email);
        Assert.Equal(MemberRole.Member, invite.Role);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.True(invite.ExpiredAt > invite.CreatedAt);
    }

    [Fact]
    public void TryAccept_Succeeds_WhenValid()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = OrgInvite.Create(orgId, "test@example.com", MemberRole.Member, userId);

        var result = invite.TryAccept();

        Assert.True(result);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        Assert.NotNull(invite.AcceptedAt);
    }

    [Fact]
    public void TryAccept_Fails_WhenAlreadyAccepted()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = OrgInvite.Create(orgId, "test@example.com", MemberRole.Member, userId);
        invite.TryAccept();

        var result = invite.TryAccept();

        Assert.False(result);
    }

    [Fact]
    public void Expire_SetsStatus()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = OrgInvite.Create(orgId, "test@example.com", MemberRole.Member, userId);

        invite.Expire();

        Assert.Equal(InviteStatus.Expired, invite.Status);
    }

    [Fact]
    public void Revoke_SetsStatus()
    {
        var orgId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = OrgInvite.Create(orgId, "test@example.com", MemberRole.Member, userId);

        invite.Revoke();

        Assert.Equal(InviteStatus.Revoked, invite.Status);
    }
}

public sealed class ProjectMemberTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var projectId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var member = ProjectMember.Create(projectId, userId, MemberRole.Owner);

        Assert.NotEqual(Guid.Empty, member.Id);
        Assert.Equal(projectId, member.ProjectId);
        Assert.Equal(userId, member.UserId);
        Assert.Equal(MemberRole.Owner, member.Role);
    }
}

public sealed class ProjectInviteTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var projectId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = ProjectInvite.Create(projectId, "test@example.com", MemberRole.Member, userId);

        Assert.NotEqual(Guid.Empty, invite.Id);
        Assert.Equal(projectId, invite.ProjectId);
        Assert.Equal(InviteStatus.Pending, invite.Status);
    }

    [Fact]
    public void TryAccept_Succeeds_WhenValid()
    {
        var projectId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var invite = ProjectInvite.Create(projectId, "test@example.com", MemberRole.Member, userId);

        var result = invite.TryAccept();

        Assert.True(result);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
    }
}

public sealed class ApiTokenTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var projectId = Guid.CreateVersion7();
        var token = ApiToken.Create("Test Token", "hash", "bcn_xxxx", projectId, ApiTokenCapability.TaskRead, null, null);

        Assert.NotEqual(Guid.Empty, token.Id);
        Assert.Equal("Test Token", token.Name);
        Assert.Equal("hash", token.TokenHash);
        Assert.Equal("bcn_xxxx", token.TokenPrefix);
        Assert.Equal(projectId, token.ProjectId);
        Assert.Equal(ApiTokenCapability.TaskRead, token.Capabilities);
        Assert.Null(token.ExpiresAt);
        Assert.Null(token.LastUsedAt);
    }

    [Fact]
    public void HasCapability_ReturnsTrue_WhenPresent()
    {
        var token = ApiToken.Create("T", "h", "p", Guid.NewGuid(), ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite, null, null);

        Assert.True(token.HasCapability(ApiTokenCapability.TaskRead));
        Assert.True(token.HasCapability(ApiTokenCapability.TaskWrite));
        Assert.False(token.HasCapability(ApiTokenCapability.Admin));
    }

    [Fact]
    public void RecordUsage_SetsLastUsedAt()
    {
        var token = ApiToken.Create("T", "h", "p", Guid.NewGuid(), ApiTokenCapability.None, null, null);

        token.RecordUsage();

        Assert.NotNull(token.LastUsedAt);
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenNotSet()
    {
        var token = ApiToken.Create("T", "h", "p", Guid.NewGuid(), ApiTokenCapability.None, null, null);

        Assert.False(token.IsExpired);
    }

    [Fact]
    public void IsExpired_ReturnsTrue_WhenExpired()
    {
        var past = DateTime.UtcNow.AddHours(-1);
        var token = ApiToken.Create("T", "h", "p", Guid.NewGuid(), ApiTokenCapability.None, past, null);

        Assert.True(token.IsExpired);
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenFuture()
    {
        var future = DateTime.UtcNow.AddHours(1);
        var token = ApiToken.Create("T", "h", "p", Guid.NewGuid(), ApiTokenCapability.None, future, null);

        Assert.False(token.IsExpired);
    }
}
