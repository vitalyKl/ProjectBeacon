namespace ProjectBeacon.Infrastructure.Tests;

using Application.Security;
using Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

public sealed class AesSecretProtectorTests
{
    private const string KeyOne = "current-key-one-aaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyTwo = "current-key-two-aaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Seal_WithWriteV2_ProducesV2Blob_RoundTrips()
    {
        var protector = Create(current: KeyOne, env: "Production", writeV2: true);

        var packed = protector.Seal("hunter2");

        Assert.StartsWith(ISecretProtector.V2Prefix, packed);
        Assert.Equal("hunter2", protector.Open(packed));
        Assert.True(protector.TryOpen(packed, out var plain));
        Assert.Equal("hunter2", plain);
    }

    [Fact]
    public void Seal_WithWriteV1_Off_ProducesLegacyBlob_RoundTrips()
    {
        const string legacy = "legacy-key-material";
        var protector = Create(current: KeyOne, legacy: legacy, env: "Production");

        var packed = protector.Seal("legacy-plain");

        Assert.False(packed.StartsWith(ISecretProtector.V2Prefix));
        Assert.Equal("legacy-plain", protector.Open(packed));
    }

    [Fact]
    public void OldV1Ciphertext_RemainsReadable_WithLegacyKey()
    {
        const string legacy = "legacy-key-material";
        var packed = SecretBox.Seal("legacy-secret", legacy);

        var protector = Create(current: KeyOne, legacy: legacy, env: "Production", writeV2: true);

        Assert.Equal("legacy-secret", protector.Open(packed));
        Assert.True(protector.HasLegacyKey);
    }

    [Fact]
    public void V1Ciphertext_WithoutLegacyKey_FailsClosed()
    {
        var packed = SecretBox.Seal("legacy-secret", "old-key-material");
        var protector = Create(current: KeyOne, env: "Production", writeV2: true);

        Assert.False(protector.HasLegacyKey);
        Assert.Throws<CryptographicException>(() => protector.Open(packed));
        Assert.False(protector.TryOpen(packed, out var plain));
        Assert.Equal(string.Empty, plain);
    }

    [Fact]
    public void V2Ciphertext_WrongCurrentKey_Throws()
    {
        var first = Create(current: KeyOne, env: "Production", writeV2: true);
        var second = Create(current: KeyTwo, env: "Production", writeV2: true);

        var packed = first.Seal("secret");

        Assert.ThrowsAny<CryptographicException>(() => second.Open(packed));
    }

    [Fact]
    public void Rotation_V2UnreadableUnderNewKey_V1ReadableViaLegacy()
    {
        var before = Create(current: KeyOne, env: "Production", writeV2: true);
        var after = Create(current: KeyTwo, legacy: KeyOne, env: "Production", writeV2: true);

        var v2 = before.Seal("v2-secret");
        var v1 = SecretBox.Seal("v1-secret", KeyOne);

        Assert.ThrowsAny<CryptographicException>(() => after.Open(v2));
        Assert.Equal("v1-secret", after.Open(v1));
    }

    [Fact]
    public void CorruptedV2_IsNotReinterpretedAsV1()
    {
        var protector = Create(current: KeyOne, env: "Production", writeV2: true);
        var packed = protector.Seal("secret");

        // Flip the first Base64 character (changes the nonce bytes) while keeping the v2: prefix.
        var chars = packed.ToCharArray();
        chars[ISecretProtector.V2Prefix.Length] = chars[ISecretProtector.V2Prefix.Length] == 'A' ? 'B' : 'A';
        var corrupted = new string(chars);

        Assert.StartsWith(ISecretProtector.V2Prefix, corrupted);
        Assert.ThrowsAny<CryptographicException>(() => protector.Open(corrupted));
    }

    [Fact]
    public void V2Seal_SameKeyDifferentNonce_BothOpen()
    {
        var protector = Create(current: KeyOne, env: "Production", writeV2: true);

        var a = protector.Seal("same");
        var b = protector.Seal("same");

        Assert.NotEqual(a, b);
        Assert.Equal("same", protector.Open(a));
        Assert.Equal("same", protector.Open(b));
    }

    [Fact]
    public void InvalidBase64_OnV1Path_ThrowsCryptographicException()
    {
        const string legacy = "legacy-key-material";
        var protector = Create(current: KeyOne, legacy: legacy, env: "Production");

        Assert.Throws<CryptographicException>(() => protector.Open("not-base64!!"));
    }

    [Fact]
    public void TryOpen_ReturnsFalse_InsteadOfThrowing()
    {
        var protector = Create(current: KeyOne, env: "Production", writeV2: true);

        Assert.False(protector.TryOpen("garbage-not-base64", out var plain));
        Assert.Equal(string.Empty, plain);
    }

    private static AesSecretProtector Create(
        string? current = null,
        string? legacy = null,
        string env = "Development",
        bool writeV2 = false,
        string? jwt = null)
    {
        var values = new Dictionary<string, string?>();
        if (current is not null)
            values[DataKeyPolicy.ConfigKey] = current;
        if (legacy is not null)
            values[DataKeyPolicy.LegacyConfigKey] = legacy;
        if (jwt is not null)
            values["JWT:Secret"] = jwt;
        values[DataKeyPolicy.WriteV2Key] = writeV2 ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AesSecretProtector(config, new TestHostEnvironment(env), NullLogger<AesSecretProtector>.Instance);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "BeaconTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = environmentName;
    }
}
