namespace ProjectBeacon.Application.Auth;

using System.Security.Cryptography;
using System.Text;
/// <summary>
/// Mints bci_ and bcr_ tokens from 24 random bytes and stores SHA-256 as Base64.
/// </summary>
public static class OpaqueToken
{
    public const string InvitePrefix = "bci_";
    public const string ResetPrefix = "bcr_";

    public static string Generate(string prefix)
    {
        var random = RandomNumberGenerator.GetBytes(24);
        return prefix + Convert.ToHexString(random).ToLowerInvariant();
    }

    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
