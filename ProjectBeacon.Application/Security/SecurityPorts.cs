namespace ProjectBeacon.Application.Security;

using Domain.Entities.Identity;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
    string GenerateRandomPassword(int length = 32);
}

public interface ITokenIssuer
{
    string GenerateToken(User user, string secretKey, TimeSpan expiration = default, Guid? projectId = null, Guid? orgId = null);
}

public interface ISecretProtector
{
    string Seal(string plain, string keyMaterial);
    string Open(string packed, string keyMaterial);
    string KeyMaterial();
}

public interface ITotp
{
    string GenerateSecret();
    string OtpAuthUri(string secret, string account);
    bool Verify(string secret, string code, DateTime utcNow);
}
