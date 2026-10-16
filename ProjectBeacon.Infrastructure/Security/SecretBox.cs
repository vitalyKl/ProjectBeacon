namespace ProjectBeacon.Infrastructure.Security;

using Application.Security;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Seals and opens secret ciphertexts.
/// v1 (legacy): AES-GCM keyed by SHA-256 of the key material, no AAD, blob is Base64(nonce|tag|cipher).
/// v2 (current): AES-GCM keyed by an HKDF-SHA256 key with explicit public parameters, AAD-bound,
/// blob is "v2:" + Base64(nonce|tag|cipher). Version selection is exact by the "v2:" prefix
/// (Base64 never contains ':'); a failed v2 open is never retried as v1.
/// </summary>
public static class SecretBox
{
    private static readonly byte[] V2Aad = Encoding.UTF8.GetBytes("beacon/dp/v2");

    /// <summary>Public HKDF salt for the v2 key derivation. Changing it invalidates all v2 ciphertexts.</summary>
    public const string HkdfSalt = "ProjectBeacon/DataProtection/salt-v1";

    /// <summary>HKDF purpose binding: keeps the data key cryptographically distinct from JWT signing.</summary>
    public const string HkdfInfo = "ProjectBeacon/DataProtection/AesGcm/v2";

    /// <summary>Derives the 32-byte v2 AES key from key material with explicit HKDF-SHA256 parameters.</summary>
    public static byte[] DeriveKey(string keyMaterial) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(keyMaterial),
            32,
            Encoding.UTF8.GetBytes(HkdfSalt),
            Encoding.UTF8.GetBytes(HkdfInfo));

    public static bool IsV2(string packed) =>
        packed.StartsWith(ISecretProtector.V2Prefix, StringComparison.Ordinal);

    // v1 (legacy format, unchanged).

    public static string Seal(string plain, string keyMaterial)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[Encoding.UTF8.GetByteCount(plain)];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plain), cipher, tag);
        return Convert.ToBase64String(Pack(nonce, tag, cipher));
    }

    public static string Open(string packed, string keyMaterial)
    {
        var raw = Decode(packed);
        RequireFullBlob(raw);
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)), 16);
        aes.Decrypt(raw[..12], raw[28..], raw[12..28], plain);
        return Encoding.UTF8.GetString(plain);
    }

    // v2 (current format).

    public static string SealV2(string plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[Encoding.UTF8.GetByteCount(plain)];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plain), cipher, tag, V2Aad);
        return ISecretProtector.V2Prefix + Convert.ToBase64String(Pack(nonce, tag, cipher));
    }

    public static string OpenV2(string packed, byte[] key)
    {
        var raw = Decode(packed[ISecretProtector.V2Prefix.Length..]);
        RequireFullBlob(raw);
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(raw[..12], raw[28..], raw[12..28], plain, V2Aad);
        return Encoding.UTF8.GetString(plain);
    }

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
            throw new CryptographicException("Ciphertext is not valid Base64.", ex);
        }
    }

    private static void RequireFullBlob(byte[] raw)
    {
        if (raw.Length < 28)
            throw new CryptographicException("Ciphertext is truncated.");
    }
}
