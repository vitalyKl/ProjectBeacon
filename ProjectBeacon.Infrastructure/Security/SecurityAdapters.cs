namespace ProjectBeacon.Infrastructure.Security;

using Application.Security;
using Domain.Entities.Identity;

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => PasswordHasher.Hash(password);

    public bool Verify(string password, string hash) => PasswordHasher.Verify(password, hash);

    public string GenerateRandomPassword(int length = 32) => PasswordHasher.GenerateRandomPassword(length);
}

public sealed class JwtTokenIssuer : ITokenIssuer
{
    public string GenerateToken(User user, string secretKey, TimeSpan expiration = default, Guid? projectId = null, Guid? orgId = null)
        => JwtTokenService.GenerateToken(user, secretKey, expiration, projectId, orgId);
}

public sealed class AesSecretProtector : ISecretProtector
{
    public string Seal(string plain, string keyMaterial) => SecretBox.Seal(plain, keyMaterial);

    public string Open(string packed, string keyMaterial) => SecretBox.Open(packed, keyMaterial);

    public string KeyMaterial() => SecretBox.KeyMaterial();
}

public sealed class RfcTotp : ITotp
{
    public string GenerateSecret() => Totp.GenerateSecret();

    public string OtpAuthUri(string secret, string account) => Totp.OtpAuthUri(secret, account);

    public bool Verify(string secret, string code, DateTime utcNow) => Totp.Verify(secret, code, utcNow);
}
