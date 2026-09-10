namespace ProjectBeacon.Infrastructure.Security;

using System.Security.Cryptography;
using System.Text;

public static class PasswordHasher
{
    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public static bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);

    public static string GenerateRandomPassword(int length = 32)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(Enumerable.Range(0, length).Select(_ => chars[bytes[_] % chars.Length]).ToArray());
    }
}
