namespace ProjectBeacon.Infrastructure.Tests;

using Application.Security;
using Infrastructure.Security;
using System.Security.Cryptography;

public sealed class SecretBoxTests
{
    [Fact]
    public void V1RoundTrip_UnchangedFormat()
    {
        const string key = "legacy-key-material";

        var packed = SecretBox.Seal("legacy-secret", key);

        Assert.False(packed.StartsWith(ISecretProtector.V2Prefix));
        Assert.Equal("legacy-secret", SecretBox.Open(packed, key));
    }

    [Fact]
    public void V1_WrongKey_Throws()
    {
        var packed = SecretBox.Seal("legacy-secret", "key-a");

        Assert.ThrowsAny<CryptographicException>(() => SecretBox.Open(packed, "key-b"));
    }

    [Fact]
    public void V1_InvalidBase64_ThrowsCryptographicException()
    {
        Assert.Throws<CryptographicException>(() => SecretBox.Open("not-base64!!", "legacy-key-material"));
    }

    [Fact]
    public void V1_TruncatedBlob_ThrowsCryptographicException()
    {
        var packed = SecretBox.Open(
            SecretBox.Seal("x", "legacy-key-material"), "legacy-key-material");
        var shortBase64 = Convert.ToBase64String(new byte[10]);

        Assert.Equal("x", packed);
        Assert.Throws<CryptographicException>(() => SecretBox.Open(shortBase64, "legacy-key-material"));
    }

    [Fact]
    public void V2RoundTrip_WithDerivedKey()
    {
        var key = SecretBox.DeriveKey("data-protection-key-material");

        var packed = SecretBox.SealV2("v2-secret", key);

        Assert.StartsWith(ISecretProtector.V2Prefix, packed);
        Assert.True(SecretBox.IsV2(packed));
        Assert.Equal("v2-secret", SecretBox.OpenV2(packed, key));
    }

    [Fact]
    public void V2_WrongKey_Throws()
    {
        var packed = SecretBox.SealV2("v2-secret", SecretBox.DeriveKey("material-a"));

        Assert.ThrowsAny<CryptographicException>(() => SecretBox.OpenV2(packed, SecretBox.DeriveKey("material-b")));
    }

    [Fact]
    public void V2_CorruptedPayload_Throws()
    {
        var key = SecretBox.DeriveKey("data-protection-key-material");
        var packed = SecretBox.SealV2("v2-secret", key);
        var chars = packed.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';

        Assert.ThrowsAny<CryptographicException>(() => SecretBox.OpenV2(new string(chars), key));
    }

    [Fact]
    public void IsV2_V1BlobIsNeverV2()
    {
        // Base64 output never contains ':', so a v1 blob can never carry the v2 prefix.
        var v1 = SecretBox.Seal("legacy-secret", "legacy-key-material");

        Assert.False(SecretBox.IsV2(v1));
        Assert.False(SecretBox.IsV2(""));
    }

    [Fact]
    public void DeriveKey_IsDeterministic_AndPurposeBound()
    {
        Assert.Equal(SecretBox.DeriveKey("material"), SecretBox.DeriveKey("material"));
        Assert.NotEqual(SecretBox.DeriveKey("material"), SecretBox.DeriveKey("other-material"));
        Assert.Equal(32, SecretBox.DeriveKey("material").Length);
    }

    [Fact]
    public void V2Key_DiffersFromV1Key_ForSameMaterial()
    {
        const string material = "same-material";

        var v2Key = SecretBox.DeriveKey(material);
        var v1Key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));

        Assert.NotEqual(v1Key, v2Key);
    }
}
