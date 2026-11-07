namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class InviteClockTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OrgInvite_Create_AndAccept_UseExplicitClock()
    {
        var invite = OrgInvite.Create(
            Guid.NewGuid(), "person@example.test", MemberRole.Member, Guid.NewGuid(), "hash", Now);

        Assert.Equal(Now, invite.CreatedAt);
        Assert.Equal(Now.AddDays(7), invite.ExpiredAt);
        Assert.True(invite.TryAccept(Now.AddDays(7).AddTicks(-1)));
        Assert.Equal(Now.AddDays(7).AddTicks(-1), invite.AcceptedAt);
    }

    [Fact]
    public void OrgInvite_RejectsAcceptanceAfterExpiry()
    {
        var invite = OrgInvite.Create(
            Guid.NewGuid(), "person@example.test", MemberRole.Member, Guid.NewGuid(), "hash", Now);

        Assert.False(invite.TryAccept(Now.AddDays(7).AddTicks(1)));
        Assert.Null(invite.AcceptedAt);
    }

    [Fact]
    public void ProjectInvite_Create_AndAccept_UseExplicitClock()
    {
        var invite = ProjectInvite.Create(
            Guid.NewGuid(), "person@example.test", MemberRole.Member, Guid.NewGuid(), "hash", Now);

        Assert.Equal(Now, invite.CreatedAt);
        Assert.Equal(Now.AddDays(7), invite.ExpiredAt);
        Assert.True(invite.TryAccept(Now.AddDays(2)));
        Assert.Equal(Now.AddDays(2), invite.AcceptedAt);
    }

    [Fact]
    public void ProjectInvite_RejectsAcceptanceAfterExpiry()
    {
        var invite = ProjectInvite.Create(
            Guid.NewGuid(), "person@example.test", MemberRole.Member, Guid.NewGuid(), "hash", Now);

        Assert.False(invite.TryAccept(Now.AddDays(7).AddTicks(1)));
        Assert.Null(invite.AcceptedAt);
    }
}
