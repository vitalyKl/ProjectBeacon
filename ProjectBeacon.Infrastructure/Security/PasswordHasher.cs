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
        return new string(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGenerator.GetInt32(0, chars.Length)]).ToArray());
    }
}
