namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;

public static class DeviceToken
{
    public const string Prefix = "bcd_";

    public static string Generate()
    {
        var random = RandomNumberGenerator.GetBytes(24);
        return Prefix + Convert.ToHexString(random).ToLowerInvariant();
    }

    public static string Hash(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static string TokenPrefixOf(string token)
    {
        var take = Math.Min(8, token.Length);
        return token[..take];
    }

    public static bool IsDeviceToken(string value) =>
        value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
