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
    public string? TotpRecoveryCodeHashesJson { get; private set; }

    public int TotpRecoveryCodeCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TotpRecoveryCodeHashesJson))
                return 0;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(TotpRecoveryCodeHashesJson)?.Length ?? 0;
            }
            catch (System.Text.Json.JsonException)
            {
                return 0;
            }
        }
    }

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
        TotpRecoveryCodeHashesJson = null;
    }

    /// <summary>Stores only one-way hashes of one-time operator recovery codes.</summary>
    public void SetTotpRecoveryCodeHashes(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var values = hashes.ToArray();
        if (values.Length > 20 || values.Any(hash => hash is null || hash.Length != 64
            || hash.Any(character => !char.IsAsciiHexDigit(character))))
            throw new ArgumentException("Recovery code hashes must be at most 20 SHA-256 hex digests.", nameof(hashes));

        var normalized = values.Select(hash => hash.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length != values.Length)
            throw new ArgumentException("Recovery code hashes must be unique.", nameof(hashes));

        TotpRecoveryCodeHashesJson = normalized.Length == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(normalized);
    }

    /// <summary>Consumes one matching recovery-code hash. Call inside a locked transaction.</summary>
    public bool TryConsumeTotpRecoveryCodeHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character))
            || string.IsNullOrWhiteSpace(TotpRecoveryCodeHashesJson))
            return false;

        string[] stored;
        try
        {
            stored = System.Text.Json.JsonSerializer.Deserialize<string[]>(TotpRecoveryCodeHashesJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        byte[] expected;
        try { expected = Convert.FromHexString(hash); }
        catch (FormatException) { return false; }

        var match = -1;
        for (var index = 0; index < stored.Length; index++)
        {
            var candidate = stored[index];
            if (candidate is null || candidate.Length != 64 || candidate.Any(character => !char.IsAsciiHexDigit(character)))
                continue;
            var candidateBytes = Convert.FromHexString(candidate);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, candidateBytes))
                match = index;
        }

        if (match < 0)
            return false;

        var remaining = stored.Where((_, index) => index != match).ToArray();
        TotpRecoveryCodeHashesJson = remaining.Length == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(remaining);
        return true;
    }

    /// <summary>Replaces the stored ciphertext only (key re-encryption); does not change <see cref="TotpEnabled"/>.</summary>
    public void UpdateTotpCipher(string cipher) => TotpSecretCipher = cipher;

    public void ConfirmTotp() => TotpEnabled = true;

    public void ClearTotp()
    {
        TotpSecretCipher = string.Empty;
        TotpEnabled = false;
        TotpRecoveryCodeHashesJson = null;
    }
}
