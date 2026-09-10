namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;

public class User : Entity
{
    public User() { }

    public string Login { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public bool IsAdmin { get; private set; }
    public DateTime? LastLoginAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    public ICollection<UserSession> Sessions { get; private set; } = [];

    public static User Create(string login, string email, string passwordHash, bool isAdmin = false)
    {
        var user = Entity.New<User>();
        user.Login = login;
        user.Email = email;
        user.PasswordHash = passwordHash;
        user.IsAdmin = isAdmin;
        return user;
    }

    public void RecordLogin()
    {
        LastLoginAt = DateTime.UtcNow;
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }

    public void RecordFailedLogin()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
        {
            LockedUntil = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        }
    }

    public bool IsLockedOut => LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;
}
