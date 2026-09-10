namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;

public class UserSession : Entity
{
    public UserSession() { }

    public Guid UserId { get; private set; }
    public string IpAddress { get; private set; } = string.Empty;
    public string? UserAgent { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public bool IsActive { get; private set; }

    public User User { get; private set; } = null!;

    public static UserSession Create(Guid userId, string ipAddress, string? userAgent = null, TimeSpan? ttl = null)
    {
        var session = Entity.New<UserSession>();
        session.UserId = userId;
        session.IpAddress = ipAddress;
        session.UserAgent = userAgent;
        session.CreatedAt = DateTime.UtcNow;
        session.ExpiresAt = DateTime.UtcNow + (ttl ?? TimeSpan.FromHours(24));
        session.IsActive = true;
        return session;
    }
}
