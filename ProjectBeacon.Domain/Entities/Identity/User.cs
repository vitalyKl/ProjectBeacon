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
    public string? OperatorRole { get; private set; }
    public DateTime? DisabledAt { get; private set; }
    public int SessionVersion { get; private set; }
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
        user.OperatorRole = isAdmin ? "SystemAdministrator" : null;
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
        BumpSessionVersion();
    }

    public void Disable(DateTime? disabledAtUtc = null)
    {
        if (DisabledAt.HasValue)
            return;

        DisabledAt = disabledAtUtc ?? DateTime.UtcNow;
        BumpSessionVersion();
    }

    public void Enable()
    {
        if (!DisabledAt.HasValue)
            return;

        DisabledAt = null;
        BumpSessionVersion();
    }

    public void SetAdmin(bool isAdmin)
    {
        if (isAdmin)
        {
            AssignOperatorRole("SystemAdministrator");
            return;
        }

        RemoveOperatorRole();
    }

    /// <summary>Assigns a persisted Control Plane role and invalidates all existing sessions.</summary>
    public void AssignOperatorRole(string role)
    {
        var normalized = role?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 32)
            throw new ArgumentException("A valid operator role key is required.", nameof(role));

        var isSystemAdministrator = string.Equals(
            normalized, "SystemAdministrator", StringComparison.Ordinal);
        if (string.Equals(OperatorRole, normalized, StringComparison.Ordinal)
            && IsAdmin == isSystemAdministrator)
            return;

        OperatorRole = normalized;
        IsAdmin = isSystemAdministrator;
        BumpSessionVersion();
    }

    /// <summary>Removes platform-operator access and invalidates every existing session.</summary>
    public void RemoveOperatorRole()
    {
        if (!IsAdmin && OperatorRole is null)
            return;

        IsAdmin = false;
        OperatorRole = null;
        BumpSessionVersion();
    }

    /// <summary>
    /// Invalidates every cookie/JWT issued before this security-sensitive account change.
    /// </summary>
    public void BumpSessionVersion()
    {
        checked { SessionVersion++; }
    }

    public bool IsDisabled => DisabledAt.HasValue;

    public bool IsLockedOut => LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;

    public void SetChatModel(Guid? backendId) => ChatModelBackendId = backendId;

    public void BeginTotp(string cipher)
    {
        TotpSecretCipher = cipher;
        TotpEnabled = false;
    }

    /// <summary>Replaces the stored ciphertext only (key re-encryption); does not change <see cref="TotpEnabled"/>.</summary>
    public void UpdateTotpCipher(string cipher) => TotpSecretCipher = cipher;

    public void ConfirmTotp() => TotpEnabled = true;

    public void ClearTotp()
    {
        TotpSecretCipher = string.Empty;
        TotpEnabled = false;
    }
}
