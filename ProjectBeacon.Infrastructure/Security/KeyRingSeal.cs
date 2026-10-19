namespace ProjectBeacon.Infrastructure.Security;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Seals Data Protection key-ring elements at rest. Blob format:
/// "kr1:" + Base64(nonce | tag | ciphertext), AES-GCM under an HKDF-SHA256 key derived
/// from DATA_PROTECTION_KEY material with a purpose-specific info, AAD-bound to the
/// format prefix. A blob that does not carry the "kr1:" prefix is rejected; a failed
/// open throws <see cref="CryptographicException"/> and never falls back silently.
/// </summary>
public static class KeyRingSeal
{
    public const string FormatPrefix = "kr1:";
    private const string HkdfInfo = "ProjectBeacon/DataProtection/KeyRingSeal/v1";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("ProjectBeacon/KeyRingSeal/kr1");

    public static string Seal(string plaintext, string keyMaterial)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(keyMaterial), 16);
        aes.Encrypt(nonce, plain, cipher, tag, Aad);
        return FormatPrefix + Convert.ToBase64String(Pack(nonce, tag, cipher));
    }

    public static string Open(string blob, string keyMaterial)
    {
        if (!blob.StartsWith(FormatPrefix, StringComparison.Ordinal))
            throw new CryptographicException("Not a key ring seal blob.");
        var raw = Decode(base64: blob[FormatPrefix.Length..]);
        if (raw.Length < 28)
            throw new CryptographicException("Key ring seal blob is truncated.");
        var plaintext = new byte[raw.Length - 28];
        using var aes = new AesGcm(DeriveKey(keyMaterial), 16);
        aes.Decrypt(raw[..12], raw[28..], raw[12..28], plaintext, Aad);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string keyMaterial) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(keyMaterial),
            32,
            Encoding.UTF8.GetBytes(SecretBox.HkdfSalt),
            Encoding.UTF8.GetBytes(HkdfInfo));

    private static byte[] Pack(byte[] nonce, byte[] tag, byte[] cipher)
    {
        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        cipher.CopyTo(packed, nonce.Length + tag.Length);
        return packed;
    }

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Key ring seal blob is not valid Base64.", ex);
        }
    }
}
