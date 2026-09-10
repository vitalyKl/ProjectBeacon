namespace ProjectBeacon.Domain.Entities.Identity;

using Common;
using Enums;

public class OrgMember : Entity, IOrgScoped
{
    public OrgMember() { }

    public Guid OrgId { get; private set; }
    public Guid UserId { get; private set; }
    public MemberRole Role { get; private set; }
    public DateTime JoinedAt { get; private set; }

    public Org Org { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static OrgMember Create(Guid orgId, Guid userId, MemberRole role)
    {
        var member = Entity.New<OrgMember>();
        member.OrgId = orgId;
        member.UserId = userId;
        member.Role = role;
        member.JoinedAt = DateTime.UtcNow;
        return member;
    }
}
