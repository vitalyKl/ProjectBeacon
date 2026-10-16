namespace ProjectBeacon.Infrastructure.Security;

using Application.Security;
using Domain.Entities.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
/// <summary>
/// IPasswordHasher adapter over PasswordHasher.
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => PasswordHasher.Hash(password);

    public bool Verify(string password, string hash) => PasswordHasher.Verify(password, hash);

    public string GenerateRandomPassword(int length = 32) => PasswordHasher.GenerateRandomPassword(length);
}
/// <summary>
/// ITokenIssuer adapter over JwtTokenService.
/// </summary>
public sealed class JwtTokenIssuer : ITokenIssuer
{
    public string GenerateToken(User user, string secretKey, TimeSpan expiration = default, Guid? projectId = null, Guid? orgId = null)
        => JwtTokenService.GenerateToken(user, secretKey, expiration, projectId, orgId);
}
/// <summary>
/// ISecretProtector adapter over SecretBox. Writes use the current key in v2 format, or the legacy
/// material in v1 format while DATA_PROTECTION_WRITE_V2 is off. Reads are version-selected by the
/// "v2:" prefix: v2 uses the current key, v1 the legacy key, never the other way around.
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private readonly byte[] _currentKey;
    private readonly string? _legacyMaterial;
    private readonly bool _writeV2;

    public AesSecretProtector(IConfiguration configuration, IHostEnvironment environment, ILogger<AesSecretProtector> logger)
    {
        var keys = DataKeyPolicy.ResolveDataKeys(configuration, environment.EnvironmentName);
        _currentKey = SecretBox.DeriveKey(keys.Current);
        _legacyMaterial = keys.Legacy;
        _writeV2 = keys.WriteV2;
        if (keys.LegacySource == DataKeyPolicy.LegacySource.JwtBridge)
            logger.LogWarning(
                "v1 ciphertexts are readable through the JWT:Secret bridge. Set DATA_PROTECTION_LEGACY_KEY " +
                "and complete the v1-to-v2 migration to end the bridge.");
    }

    public bool HasLegacyKey => _legacyMaterial is not null;

    public bool WriteV2 => _writeV2;

    public string Seal(string plain)
    {
        if (_writeV2)
            return SecretBox.SealV2(plain, _currentKey);
        if (_legacyMaterial is null)
            throw new InvalidOperationException("No legacy key material configured; enable DATA_PROTECTION_WRITE_V2.");
        return SecretBox.Seal(plain, _legacyMaterial);
    }

    public string Open(string packed)
    {
        if (SecretBox.IsV2(packed))
            return SecretBox.OpenV2(packed, _currentKey);
        if (_legacyMaterial is null)
            throw new CryptographicException("Ciphertext is v1 and no legacy key material is configured.");
        return SecretBox.Open(packed, _legacyMaterial);
    }

    public bool TryOpen(string packed, out string plain)
    {
        try
        {
            plain = Open(packed);
            return true;
        }
        catch (CryptographicException)
        {
            plain = string.Empty;
            return false;
        }
    }
}
/// <summary>
/// ITOTP adapter over Totp.
/// </summary>
public sealed class RfcTotp : ITotp
{
    public string GenerateSecret() => Totp.GenerateSecret();

    public string OtpAuthUri(string secret, string account) => Totp.OtpAuthUri(secret, account);

    public bool Verify(string secret, string code, DateTime utcNow) => Totp.Verify(secret, code, utcNow);
}
