namespace ProjectBeacon.Domain.Entities.Projects;

using Common;
using Enums;
using Identity;

public class ProjectMember : Entity, IProjectScoped
{
    public ProjectMember() { }

    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public MemberRole Role { get; private set; }
    public DateTime JoinedAt { get; private set; }

    public Project Project { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static ProjectMember Create(Guid projectId, Guid userId, MemberRole role)
    {
        var member = Entity.New<ProjectMember>();
        member.ProjectId = projectId;
        member.UserId = userId;
        member.Role = role;
        member.JoinedAt = DateTime.UtcNow;
        return member;
    }
}
