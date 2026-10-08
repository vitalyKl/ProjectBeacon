namespace ProjectBeacon.Domain.Enums;

/// <summary>Project or org membership. <see cref="Owner"/> and <see cref="Admin"/> can administer the project.</summary>
    public enum MemberRole
{
    Owner = 0,
    Admin = 1,
    Member = 2
}
