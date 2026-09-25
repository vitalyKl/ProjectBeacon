namespace ProjectBeacon.Infrastructure.Security;

using System.Security.Cryptography;
using System.Text;

public static class SecretBox
{
    public static string Seal(string plain, string keyMaterial)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[Encoding.UTF8.GetByteCount(plain)];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plain), cipher, tag);
        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        cipher.CopyTo(packed, nonce.Length + tag.Length);
        return Convert.ToBase64String(packed);
    }

    public static string Open(string packed, string keyMaterial)
    {
        var raw = Convert.FromBase64String(packed);
        var nonce = raw[..12];
        var tag = raw[12..28];
        var cipher = raw[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)), 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static string KeyMaterial() =>
        Environment.GetEnvironmentVariable("JWT__Secret")
        ?? Environment.GetEnvironmentVariable("JWT_SECRET")
        ?? "beacon-dev-data-key";
}
