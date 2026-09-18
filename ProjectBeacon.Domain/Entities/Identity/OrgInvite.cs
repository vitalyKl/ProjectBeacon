namespace ProjectBeacon.Domain.Entities.Identity;

using Common;
using Enums;

public class OrgInvite : Entity, IOrgScoped
{
    public OrgInvite() { }

    public Guid OrgId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public MemberRole Role { get; private set; }
    public InviteStatus Status { get; private set; }
    public Guid? InvitedByUserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime? AcceptedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiredAt { get; private set; }

    public Org Org { get; private set; } = null!;

    public static OrgInvite Create(Guid orgId, string email, MemberRole role, Guid invitedByUserId, string tokenHash)
    {
        var invite = Entity.New<OrgInvite>();
        invite.OrgId = orgId;
        invite.Email = NormalizeEmail(email);
        invite.Role = role;
        invite.Status = InviteStatus.Pending;
        invite.InvitedByUserId = invitedByUserId;
        invite.TokenHash = tokenHash;
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

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
