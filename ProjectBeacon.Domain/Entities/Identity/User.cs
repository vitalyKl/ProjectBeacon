namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;

/// <summary>
/// Account. Stores a password hash, not the password. Five failed logins lock the account for 15 minutes.
/// TOTP is stored as <see cref="TotpSecretCipher"/> and stays disabled until <see cref="ConfirmTotp"/>.
/// </summary>
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
    public Guid? ChatModelBackendId { get; private set; }
    public string TotpSecretCipher { get; private set; } = string.Empty;
    public bool TotpEnabled { get; private set; }

    public ICollection<UserSession> Sessions { get; private set; } = [];

    public static User Create(string login, string email, string passwordHash, bool isAdmin = false)
    {
        var user = Entity.New<User>();
        user.Login = login.Trim();
        user.Email = email.Trim().ToLowerInvariant();
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

    /// <summary>Counts a failed login. At five failures, sets <see cref="LockedUntil"/> to 15 minutes from now.</summary>
    public void RecordFailedLogin()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
        {
            LockedUntil = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        }
    }

    public void ResetPassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }

    public bool IsLockedOut => LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;

    public void SetChatModel(Guid? backendId) => ChatModelBackendId = backendId;

    public void BeginTotp(string cipher)
    {
        TotpSecretCipher = cipher;
        TotpEnabled = false;
    }

    public void ConfirmTotp() => TotpEnabled = true;

    public void ClearTotp()
    {
        TotpSecretCipher = string.Empty;
        TotpEnabled = false;
    }
}
