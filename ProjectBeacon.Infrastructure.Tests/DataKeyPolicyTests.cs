namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Security;
using Microsoft.Extensions.Configuration;

public sealed class DataKeyPolicyTests
{
    private static readonly string Key32 = new('a', 32);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Production_MissingCurrentKey_Throws(string? raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(raw, env: "Production"));

        Assert.Contains("DATA_PROTECTION_KEY", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Production_ShortCurrentKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(new string('a', 31), env: "Production"));

        Assert.Contains("31", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("Production ")]
    public void Production_CaseInsensitive_Throws(string env)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(null, env: env));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Unknown")]
    [InlineData("staging")]
    public void NonSafeEnvironment_MissingCurrentKey_Throws(string env)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(null, env: env));
    }

    [Fact]
    public void Production_WriteV2Off_WithoutLegacyMaterial_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(Key32, env: "Production"));

        Assert.Contains("DATA_PROTECTION_WRITE_V2", ex.Message);
    }

    [Fact]
    public void Production_WriteV2On_WithoutLegacyMaterial_ResolvesWithoutLegacy()
    {
        var keys = Resolve(Key32, writeV2: "true", env: "Production");

        Assert.Equal(Key32, keys.Current);
        Assert.Null(keys.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.None, keys.LegacySource);
        Assert.True(keys.WriteV2);
    }

    [Fact]
    public void Production_ExplicitLegacy_BeatsJwtBridge()
    {
        var keys = Resolve(Key32, legacy: "explicit-legacy", jwt: "jwt-secret-value", env: "Production");

        Assert.Equal("explicit-legacy", keys.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.Explicit, keys.LegacySource);
    }

    [Fact]
    public void Production_JwtBridge_WhenNoExplicitLegacy()
    {
        var keys = Resolve(Key32, jwt: "jwt-secret-value", env: "Production");

        Assert.Equal("jwt-secret-value", keys.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.JwtBridge, keys.LegacySource);
    }

    [Fact]
    public void LegacyKey_IsNotLengthValidated()
    {
        var keys = Resolve(Key32, legacy: "short", env: "Production");

        Assert.Equal("short", keys.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.Explicit, keys.LegacySource);
    }

    [Fact]
    public void SafeMode_MissingCurrentKey_UsesDevFallback()
    {
        var keys = Resolve(null, env: "Development");

        Assert.Equal(DataKeyPolicy.DevFallbackKey, keys.Current);
        Assert.Equal(DataKeyPolicy.DevFallbackKey, keys.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.DevFallback, keys.LegacySource);
    }

    [Fact]
    public void SafeMode_SetCurrentKey_ReturnsIt()
    {
        var keys = Resolve("dev-current-key", env: "Testing");

        Assert.Equal("dev-current-key", keys.Current);
    }

    [Fact]
    public void SafeMode_LegacyResolvesJwtThenDevFallback()
    {
        var withJwt = Resolve(null, jwt: "dev-jwt", env: "Development");
        Assert.Equal("dev-jwt", withJwt.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.JwtBridge, withJwt.LegacySource);

        var withoutJwt = Resolve(null, env: "Development");
        Assert.Equal(DataKeyPolicy.DevFallbackKey, withoutJwt.Legacy);
        Assert.Equal(DataKeyPolicy.LegacySource.DevFallback, withoutJwt.LegacySource);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public void WriteV2_FlagParsing(string raw)
    {
        Assert.True(Resolve(Key32, writeV2: raw, env: "Production").WriteV2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("0")]
    public void WriteV2_DefaultsOff(string? raw)
    {
        // Safe mode: no legacy material needed when writes stay on the legacy key.
        Assert.False(Resolve(null, writeV2: raw, env: "Development").WriteV2);
    }

    private static DataKeyPolicy.DataKeys Resolve(
        string? current,
        string? legacy = null,
        string? jwt = null,
        string? writeV2 = null,
        string env = "Development")
    {
        var values = new Dictionary<string, string?>();
        if (current is not null)
            values[DataKeyPolicy.ConfigKey] = current;
        if (legacy is not null)
            values[DataKeyPolicy.LegacyConfigKey] = legacy;
        if (jwt is not null)
            values["JWT:Secret"] = jwt;
        if (writeV2 is not null)
            values[DataKeyPolicy.WriteV2Key] = writeV2;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return DataKeyPolicy.ResolveDataKeys(config, env);
    }
}
