namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Security;

public sealed class DataKeyPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Production_MissingKey_Throws(string? raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataKeyPolicy.EnsureConfigured(raw, "Production"));

        Assert.Contains("32", ex.Message);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("Production ")]
    public void Production_CaseInsensitive_Throws(string env)
    {
        Assert.Throws<InvalidOperationException>(() => DataKeyPolicy.EnsureConfigured(null, env));
    }

    [Fact]
    public void Production_ShortKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DataKeyPolicy.EnsureConfigured(new string('a', 31), "Production"));

        Assert.Contains("31", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Production_ExactMinimum_ReturnsIt()
    {
        var key = new string('a', 32);

        Assert.Equal(key, DataKeyPolicy.EnsureConfigured(key, "Production"));
    }

    [Fact]
    public void Production_Long_ReturnsIt()
    {
        var key = "ProjectBeacon-Data-Key-Must-Be-At-Least-32-Characters-Long";

        Assert.Equal(key, DataKeyPolicy.EnsureConfigured(key, "Production"));
    }

    [Theory]
    [InlineData(null, "Development")]
    [InlineData("", "Development")]
    [InlineData(null, "Testing")]
    [InlineData("", "Testing")]
    [InlineData(null, "development")]
    [InlineData(null, "TESTING")]
    public void SafeMode_MissingKey_ReturnsDevFallback(string? raw, string env)
    {
        Assert.Equal(DataKeyPolicy.DevFallbackKey, DataKeyPolicy.EnsureConfigured(raw, env));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void SafeMode_SetKey_ReturnsIt(string env)
    {
        Assert.Equal("somedevkey", DataKeyPolicy.EnsureConfigured("somedevkey", env));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("staging")]
    public void NonSafeEnvironment_MissingKey_Throws(string env)
    {
        Assert.Throws<InvalidOperationException>(() => DataKeyPolicy.EnsureConfigured(null, env));
    }

    [Fact]
    public void Staging_ShortKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DataKeyPolicy.EnsureConfigured(new string('a', 10), "Staging"));
    }
}
