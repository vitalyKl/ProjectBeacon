namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;

public class PasswordResetToken : Entity
{
    public PasswordResetToken() { }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }

    public User User { get; private set; } = null!;

    public static PasswordResetToken Create(Guid userId, string tokenHash, TimeSpan? ttl = null)
    {
        var token = Entity.New<PasswordResetToken>();
        token.UserId = userId;
        token.TokenHash = tokenHash;
        token.ExpiresAt = DateTime.UtcNow + (ttl ?? TimeSpan.FromHours(1));
        return token;
    }

    public bool TryConsume()
    {
        if (UsedAt is not null || ExpiresAt < DateTime.UtcNow)
            return false;
        UsedAt = DateTime.UtcNow;
        return true;
    }
}
