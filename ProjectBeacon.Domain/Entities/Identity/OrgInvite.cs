namespace ProjectBeacon.Domain.Entities.Identity;

using Common;
using Enums;

/// <summary>
/// An org-scoped invitation. Stores <see cref="TokenHash"/> only, never a raw invite token.
/// Created pending and expires seven days after <see cref="Create"/>.
/// </summary>
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

    public static OrgInvite Create(Guid orgId, string email, MemberRole role, Guid invitedByUserId, string tokenHash) => Create(orgId, email, role, invitedByUserId, tokenHash, DateTime.UtcNow);

    public static OrgInvite Create
    {
        var invite = Entity.New<OrgInvite>();
        invite.OrgId = orgId;
        invite.Email = NormalizeEmail(email);
        invite.Role = role;
        invite.Status = InviteStatus.Pending;
        invite.InvitedByUserId = invitedByUserId;
        invite.TokenHash = tokenHash;
        invite.CreatedAt = now;
        invite.ExpiredAt = now + TimeSpan.FromDays(7);
        return invite;
    }

    public bool TryAccept() => TryAccept(DateTime.UtcNow);

    public bool TryAccept(DateTime now)
    {
        if (Status != InviteStatus.Pending || ExpiredAt < now)
            return false;

        Status = InviteStatus.Accepted;
        AcceptedAt = now;
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
