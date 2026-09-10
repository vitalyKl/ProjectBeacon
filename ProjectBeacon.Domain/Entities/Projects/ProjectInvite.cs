namespace ProjectBeacon.Domain.Entities.Projects;

using Common;
using Enums;

public class ProjectInvite : Entity, IProjectScoped
{
    public ProjectInvite() { }

    public Guid ProjectId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public MemberRole Role { get; private set; }
    public InviteStatus Status { get; private set; }
    public Guid? InvitedByUserId { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiredAt { get; private set; }

    public Project Project { get; private set; } = null!;

    public static ProjectInvite Create(Guid projectId, string email, MemberRole role, Guid invitedByUserId)
    {
        var invite = Entity.New<ProjectInvite>();
        invite.ProjectId = projectId;
        invite.Email = email;
        invite.Role = role;
        invite.Status = InviteStatus.Pending;
        invite.InvitedByUserId = invitedByUserId;
        invite.CreatedAt = DateTime.UtcNow;
        invite.ExpiredAt = DateTime.UtcNow + TimeSpan.FromDays(7);
        return invite;
    }

    public bool TryAccept()
    {
        if (Status != InviteStatus.Pending || ExpiredAt < DateTime.UtcNow)
            return false;

        Status = InviteStatus.Accepted;
        AcceptedAt = DateTime.UtcNow;
        return true;
    }

    public void Expire()
    {
        Status = InviteStatus.Expired;
    }

    public void Revoke()
    {
        Status = InviteStatus.Revoked;
    }
}
