namespace ProjectBeacon.Application.Security;

using Domain.Entities.Identity;

/// <summary>Hashes and verifies passwords, and can generate a random password.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
    string GenerateRandomPassword(int length = 32);
}

/// <summary>Issues a token for a user, optionally scoped to a project or org.</summary>
public interface ITokenIssuer
{
    string GenerateToken(User user, string secretKey, TimeSpan expiration = default, Guid? projectId = null, Guid? orgId = null);
}

/// <summary>
/// Seals and opens stored secrets. Version is carried by the ciphertext itself: blobs starting
/// with <see cref="V2Prefix"/> are v2 (current key); anything else is v1 (legacy key material
/// only). Key material never leaves the protector.
/// </summary>
public interface ISecretProtector
{
    public const string V2Prefix = "v2:";

    string Seal(string plain);
    string Open(string packed);
    bool TryOpen(string packed, out string plain);
    bool HasLegacyKey { get; }

    /// <summary>
    /// True when <see cref="Seal"/> writes v2 ciphertexts. While false, seals stay v1 and the
    /// one-shot re-encryption pass is a no-op.
    /// </summary>
    bool WriteV2 { get; }
}

/// <summary>Creates a TOTP secret, its otpauth URI, and verifies a code at a UTC time.</summary>
public interface ITotp
{
    string GenerateSecret();
    string OtpAuthUri(string secret, string account);
    bool Verify(string secret, string code, DateTime utcNow);
}
