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

/// <summary>Seals and opens a secret and supplies the key material used to do so.</summary>
public interface ISecretProtector
{
    string Seal(string plain, string keyMaterial);
    string Open(string packed, string keyMaterial);
    string KeyMaterial();
}

/// <summary>Creates a TOTP secret, its otpauth URI, and verifies a code at a UTC time.</summary>
public interface ITotp
{
    string GenerateSecret();
    string OtpAuthUri(string secret, string account);
    bool Verify(string secret, string code, DateTime utcNow);
}
